using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace YMM4Echo;

/// <summary>
/// After Effects「エコー」を忠実に再現する映像エフェクトプロセッサ。
/// 毎フレームの入力を整数ピクセル境界で劣化なくリングバッファに保存し、
/// サブフレーム補間・VRAMプール管理・階層合成により高精度かつ安定して動作する。
/// </summary>
internal sealed class EchoEffectProcessor : IVideoEffectProcessor
{
    /// <summary>保持する履歴フレーム数の上限。</summary>
    const int MaxHistory = 1024;

    /// <summary>履歴ビットマップ全体のVRAM使用量上限（約1.5GB）。超過時は古いスロットから自動解放する。</summary>
    const long MaxVramBytes = 1536L * 1024L * 1024L;

    /// <summary>再利用プールに保持する余剰ビットマップの最大数。</summary>
    const int MaxPoolCount = 16;

    /// <summary>プレビューのコマ落ち時に前後を探索する最大フレーム幅。</summary>
    const int FallbackSearchRadius = 8;

    readonly IGraphicsDevicesAndContext devices;
    readonly EchoEffect item;

    /// <summary>Outputを固定するための素通しエフェクト。内部の合成チェーンは毎フレーム組み替える。</summary>
    readonly AffineTransform2D passthrough;

    readonly ID2D1Image output;
    readonly ID2D1Bitmap1 transparent;

    readonly Slot[] slots = new Slot[MaxHistory];
    readonly List<object> chain = new();
    readonly List<PooledBitmap> bitmapPool = new();

    /// <summary>YMM4本体の DeviceContext は描画中のことがあり、BeginDraw できない。同じ Device 上に専用 DC を持つ。</summary>
    ID2D1DeviceContext? captureDc;

    ID2D1Image? input;
    int? lastFrame;
    long totalVramBytes;

    readonly struct PooledBitmap
    {
        public readonly ID2D1Bitmap1 Bitmap;
        public readonly int Width;
        public readonly int Height;
        public long Bytes => (long)Width * Height * 4L;

        public PooledBitmap(ID2D1Bitmap1 bitmap, int width, int height)
        {
            Bitmap = bitmap;
            Width = width;
            Height = height;
        }
    }

    readonly struct SlotTransform
    {
        public readonly Vector2 DrawPoint;
        public readonly Vector2 Zoom;
        public readonly Vector3 Rotation;
        public readonly Vector2 CenterPoint;
        public readonly float Opacity;
        public readonly bool Invert;

        public SlotTransform(DrawDescription d)
        {
            DrawPoint = d.DrawPoint;
            Zoom = d.Zoom;
            Rotation = d.Rotation;
            CenterPoint = d.CenterPoint;
            Opacity = (float)Math.Clamp(d.Opacity, 0.0, 1.0);
            Invert = d.Invert;
        }

        public SlotTransform(Vector2 drawPoint, Vector2 zoom, Vector3 rotation, Vector2 centerPoint, float opacity, bool invert)
        {
            DrawPoint = drawPoint;
            Zoom = zoom;
            Rotation = rotation;
            CenterPoint = centerPoint;
            Opacity = opacity;
            Invert = invert;
        }

        public static SlotTransform Lerp(SlotTransform a, SlotTransform b, float t)
        {
            return new SlotTransform(
                Vector2.Lerp(a.DrawPoint, b.DrawPoint, t),
                Vector2.Lerp(a.Zoom, b.Zoom, t),
                Vector3.Lerp(a.Rotation, b.Rotation, t),
                Vector2.Lerp(a.CenterPoint, b.CenterPoint, t),
                a.Opacity + (b.Opacity - a.Opacity) * t,
                t < 0.5f ? a.Invert : b.Invert
            );
        }

        public Matrix3x2 ToMatrix()
        {
            var centerShift = CenterPoint == Vector2.Zero
                ? Matrix3x2.Identity
                : Matrix3x2.CreateTranslation(-CenterPoint);
            var flip = Invert
                ? Matrix3x2.CreateScale(-1f, 1f)
                : Matrix3x2.Identity;
            return centerShift
                * Matrix3x2.CreateScale(Zoom)
                * flip
                * Matrix3x2.CreateRotation(Rotation.Z * MathF.PI / 180f)
                * Matrix3x2.CreateTranslation(DrawPoint);
        }
    }

    sealed class Slot
    {
        public ID2D1Bitmap1? Bitmap;
        public int Frame = int.MinValue;
        public int Width, Height;
        public float Left, Top;
        public SlotTransform Transform;
        public Matrix3x2 Placement;

        public long Bytes => Bitmap is null ? 0L : (long)Width * Height * 4L;
    }

    public ID2D1Image Output => output;

    public unsafe EchoEffectProcessor(IGraphicsDevicesAndContext devices, EchoEffect item)
    {
        this.devices = devices;
        this.item = item;
        for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
        uint pixel = 0;
        transparent = devices.DeviceContext.CreateBitmap(new SizeI(1, 1), (IntPtr)(&pixel), 4,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        passthrough = new AffineTransform2D(devices.DeviceContext);
        passthrough.SetInput(0, transparent, true);
        output = passthrough.Output;
    }

    public void SetInput(ID2D1Image? input) => this.input = input;

    public void ClearInput()
    {
        input = null;
        passthrough.SetInput(0, transparent, true);
        DisposeSlots();
        DisposePool();
        lastFrame = null;
    }

    public DrawDescription Update(EffectDescription effectDescription)
    {
        // アニメーション値はアイテム内時間で評価する。
        var frame = effectDescription.ItemPosition.Frame;
        // 履歴は時間制御の影響を受けないタイムライン上の時刻で管理する。
        var historyFrame = effectDescription.TimelinePosition.Frame;
        var length = effectDescription.ItemDuration.Frame;
        var fps = effectDescription.FPS;
        var dc = devices.DeviceContext;
        var desc = effectDescription.DrawDescription;

        ReleaseChain();

        if (input is null)
        {
            passthrough.SetInput(0, transparent, true);
            return desc;
        }

        int echoCount = Math.Clamp((int)Math.Round(item.EchoCount.GetValue(frame, length, fps)), 0, MaxHistory - 1);
        double echoTime = item.EchoTime.GetValue(frame, length, fps);
        float start = (float)Math.Max(0, item.StartIntensity.GetValue(frame, length, fps));
        float decay = (float)Math.Max(0, item.Decay.GetValue(frame, length, fps));
        double step = Math.Abs(echoTime) * fps;

        // シーク（巻き戻しや大きなジャンプ）時は現在位置より未来のスロットを無効化する
        if (lastFrame.HasValue && (historyFrame < lastFrame.Value || Math.Abs(historyFrame - lastFrame.Value) > 60))
        {
            InvalidateFutureSlots(historyFrame);
        }
        lastFrame = historyFrame;

        // 必要範囲外の古いビットマップを解放・プール返却してVRAM消費を最小限に抑える
        int requiredSpan = Math.Min(MaxHistory, (int)Math.Ceiling(echoCount * step) + 2);
        int keepSpan = Math.Clamp(requiredSpan + 32, 64, MaxHistory);
        ReclaimUnusedSlots(historyFrame, keepSpan);

        // 現在フレームを履歴に保存。失敗したらエフェクトなしで素通し
        if (!Capture(dc, historyFrame, desc))
        {
            passthrough.SetInput(0, input, true);
            return desc;
        }

        // アイテム開始位置（タイムライン上）。AEと同様にレイヤー開始より前はエコーを出さない
        int itemStartFrame = historyFrame - frame;
        var currentTransform = new SlotTransform(desc);
        var currentPlacement = currentTransform.ToMatrix();

        // 現在フレームの拡大率が極小/ゼロの場合は逆行列が破綻するため、ワールド座標で直接出力する
        Matrix3x2 inverseCurrentPlacement = Matrix3x2.Identity;
        bool useWorldFallback = MathF.Abs(currentTransform.Zoom.X) <= 1e-3f
            || MathF.Abs(currentTransform.Zoom.Y) <= 1e-3f
            || !Matrix3x2.Invert(currentPlacement, out inverseCurrentPlacement);

        if (useWorldFallback)
        {
            inverseCurrentPlacement = Matrix3x2.Identity;
        }

        var layers = new List<(Slot? slot, bool isCurrentInput, Matrix3x2 placement, float weight)>(echoCount + 1);
        for (int k = 0; k <= echoCount; k++)
        {
            float baseWeight = start * MathF.Pow(decay, k);
            if (baseWeight <= 1e-5f)
            {
                if (decay <= 1.0f) break; // これ以降も0以下なので打ち切り
                continue;
            }

            double offset = k * step;
            if (offset >= MaxHistory) break;

            double target = historyFrame - offset;
            if (target < itemStartFrame) break; // アイテム開始前は存在しない

            // 現在フレームと完全に同一時刻なら元のinputを劣化なしで直接使う
            if (offset == 0.0)
            {
                float weight = baseWeight * currentTransform.Opacity;
                if (weight > 1e-5f)
                {
                    layers.Add((null, true, currentPlacement, weight));
                }
                continue;
            }

            if (!TrySampleHistory(target, itemStartFrame, historyFrame, out var sampleSlot, out var sampleTransform))
            {
                continue;
            }

            float finalWeight = baseWeight * sampleTransform.Opacity;
            if (finalWeight <= 1e-5f) continue;

            layers.Add((sampleSlot, false, sampleTransform.ToMatrix(), finalWeight));
        }

        if (layers.Count == 0)
        {
            passthrough.SetInput(0, transparent, true);
            return desc with { Opacity = 1.0 };
        }

        // AEの「ブレンド」は全エコーの算術平均（枚数で除算）
        if (item.Operator == EchoOperator.Blend)
        {
            float invCount = 1.0f / layers.Count;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                layers[i] = (l.slot, l.isCurrentInput, l.placement, l.weight * invCount);
            }
        }

        // Screen / Maximum / Minimum ではRGBが1.0を超えるとブレンド式が反転するため出力を[0,1]にクランプする
        bool clampWeightOutput = item.Operator is EchoOperator.Screen or EchoOperator.Maximum or EchoOperator.Minimum;

        // 各エコー画像を生成（配置変換 → 強度適用）
        var images = new List<ID2D1Image>(layers.Count);
        foreach (var (slot, isCurrentInput, placement, weight) in layers)
        {
            ID2D1Image transformed;
            if (isCurrentInput)
            {
                if (useWorldFallback)
                {
                    var move = Track(new AffineTransform2D(dc) { TransformMatrix = placement });
                    move.SetInput(0, input, true);
                    transformed = Track(move.Output);
                }
                else
                {
                    // 現在フレーム本体は変換不要で劣化ゼロ
                    transformed = input;
                }
            }
            else
            {
                var matrix = Matrix3x2.CreateTranslation(slot!.Left, slot.Top) * placement * inverseCurrentPlacement;
                var move = Track(new AffineTransform2D(dc) { TransformMatrix = matrix });
                move.SetInput(0, slot.Bitmap, true);
                transformed = Track(move.Output);
            }

            images.Add(ApplyWeight(dc, transformed, weight, clampWeightOutput));
        }

        passthrough.SetInput(0, Combine(dc, images, item.Operator), true);

        // 不透明度は各エコーに個別に適用済みのため、返却するDrawDescriptionでは1.0にする
        if (useWorldFallback)
        {
            return desc with
            {
                Draw = new Vector3(0, 0, desc.Draw.Z),
                CenterPoint = Vector2.Zero,
                Zoom = Vector2.One,
                Rotation = new Vector3(desc.Rotation.X, desc.Rotation.Y, 0),
                Invert = false,
                Opacity = 1.0
            };
        }

        return desc with { Opacity = 1.0 };
    }

    /// <summary>
    /// 指定した実数フレーム時刻の履歴スロットと補間済みトランスフォームを取得する。
    /// プレビュー時のコマ落ち（欠損フレーム）にも前後探索で滑らかに対応する。
    /// </summary>
    bool TrySampleHistory(double target, int itemStartFrame, int historyFrame, out Slot sampleSlot, out SlotTransform sampleTransform)
    {
        int targetFloor = (int)Math.Floor(target);
        int targetCeil = (int)Math.Ceiling(target);

        var slotFloor = GetValidSlot(targetFloor);
        var slotCeil = targetCeil == targetFloor ? slotFloor : GetValidSlot(targetCeil);

        if (slotFloor is not null && slotCeil is not null)
        {
            if (targetFloor == targetCeil)
            {
                sampleSlot = slotFloor;
                sampleTransform = slotFloor.Transform;
                return true;
            }
            float t = (float)(target - targetFloor);
            sampleSlot = t <= 0.5f ? slotFloor : slotCeil;
            sampleTransform = SlotTransform.Lerp(slotFloor.Transform, slotCeil.Transform, t);
            return true;
        }

        // プレビューのコマ落ち等で直接の前後フレームが欠損している場合、近傍の有効スロットを探索して補間する
        Slot? older = slotFloor;
        if (older is null)
        {
            int minFrame = Math.Max(itemStartFrame, targetFloor - FallbackSearchRadius);
            for (int f = targetFloor - 1; f >= minFrame; f--)
            {
                older = GetValidSlot(f);
                if (older is not null) break;
            }
        }

        Slot? newer = slotCeil;
        if (newer is null)
        {
            int maxFrame = Math.Min(historyFrame, targetCeil + FallbackSearchRadius);
            for (int f = targetCeil + 1; f <= maxFrame; f++)
            {
                newer = GetValidSlot(f);
                if (newer is not null) break;
            }
        }

        if (older is not null && newer is not null)
        {
            if (older.Frame == newer.Frame)
            {
                sampleSlot = older;
                sampleTransform = older.Transform;
                return true;
            }
            float t = Math.Clamp((float)((target - older.Frame) / (newer.Frame - older.Frame)), 0f, 1f);
            sampleSlot = t <= 0.5f ? older : newer;
            sampleTransform = SlotTransform.Lerp(older.Transform, newer.Transform, t);
            return true;
        }

        if (older is not null)
        {
            sampleSlot = older;
            sampleTransform = older.Transform;
            return true;
        }

        if (newer is not null)
        {
            sampleSlot = newer;
            sampleTransform = newer.Transform;
            return true;
        }

        sampleSlot = null!;
        sampleTransform = default;
        return false;
    }

    Slot? GetValidSlot(int frame)
    {
        var s = slots[Mod(frame)];
        return s.Frame == frame && s.Bitmap is not null ? s : null;
    }

    /// <summary>
    /// 強度（weight）を画像に適用する。
    /// ColorMatrixを使うことで、Opacityエフェクトの1.0上限クランプを回避し、
    /// AE同様に開始強度＞1.0や減衰＞1.0のオーバーブライト表現を可能にする。
    /// </summary>
    ID2D1Image ApplyWeight(ID2D1DeviceContext dc, ID2D1Image source, float weight, bool clampOutput)
    {
        if (MathF.Abs(weight - 1.0f) <= 1e-5f)
            return source;

        // アルファが1.0を超えるとSourceOverで色が反転するため、アルファ係数は最大1.0に制限する
        float alphaWeight = MathF.Min(weight, 1.0f);
        var cm = Track(new ColorMatrix(dc)
        {
            Matrix = new Matrix5x4
            {
                M11 = weight,
                M22 = weight,
                M33 = weight,
                M44 = alphaWeight
            },
            AlphaMode = ColorMatrixAlphaMode.Premultiplied
        });
        if (clampOutput && weight > 1.0f)
        {
            // D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT = 2
            cm.SetValue(2, true);
        }
        cm.SetInput(0, source, true);
        return Track(cm.Output);
    }

    /// <summary>images[0] が現在フレーム、以降が古いエコー。</summary>
    ID2D1Image Combine(ID2D1DeviceContext dc, List<ID2D1Image> images, EchoOperator op)
    {
        if (images.Count == 1) return images[0];

        switch (op)
        {
            case EchoOperator.Add:
            case EchoOperator.Blend:
                return BatchComposite(dc, images, CompositeMode.Plus);

            case EchoOperator.CompositeInBack:
                // 現在フレームが一番手前、古いエコーほど奥（AE標準）
                var reversed = new List<ID2D1Image>(images);
                reversed.Reverse();
                return BatchComposite(dc, reversed, CompositeMode.SourceOver);

            case EchoOperator.CompositeInFront:
                // 現在フレームが一番下、古いエコーほど手前
                return BatchComposite(dc, images, CompositeMode.SourceOver);

            case EchoOperator.Maximum:
                return TreeBlend(dc, images, BlendMode.Lighten);

            case EchoOperator.Minimum:
                return TreeBlend(dc, images, BlendMode.Darken);

            case EchoOperator.Screen:
                return TreeBlend(dc, images, BlendMode.Screen);

            default:
                return images[0];
        }
    }

    /// <summary>
    /// Direct2DのCompositeエフェクトを用いて複数画像を階層的に合成する。
    /// 直列チェーンによる深い再帰・Direct2D限界を回避し、高速かつ安定した合成を行う。
    /// </summary>
    ID2D1Image BatchComposite(ID2D1DeviceContext dc, List<ID2D1Image> images, CompositeMode mode)
    {
        if (images.Count == 0) return transparent;
        if (images.Count == 1) return images[0];

        const int BatchSize = 16;
        var current = images;
        while (current.Count > 1)
        {
            var next = new List<ID2D1Image>((current.Count + BatchSize - 1) / BatchSize);
            for (int i = 0; i < current.Count; i += BatchSize)
            {
                int count = Math.Min(BatchSize, current.Count - i);
                if (count == 1)
                {
                    next.Add(current[i]);
                }
                else
                {
                    var comp = Track(new Composite(dc) { InputCount = count, Mode = mode });
                    for (int j = 0; j < count; j++)
                        comp.SetInput(j, current[i + j], true);
                    next.Add(Track(comp.Output));
                }
            }
            current = next;
        }
        return current[0];
    }

    /// <summary>
    /// 2入力のBlendエフェクトをトーナメント（二分木）方式で合成し、チェーンの深さを O(log N) に抑える。
    /// </summary>
    ID2D1Image TreeBlend(ID2D1DeviceContext dc, List<ID2D1Image> images, BlendMode mode)
    {
        if (images.Count == 0) return transparent;
        if (images.Count == 1) return images[0];

        var current = images;
        while (current.Count > 1)
        {
            var next = new List<ID2D1Image>((current.Count + 1) / 2);
            for (int i = 0; i < current.Count; i += 2)
            {
                if (i + 1 < current.Count)
                {
                    var b = Track(new Vortice.Direct2D1.Effects.Blend(dc) { Mode = mode });
                    b.SetInput(0, current[i], true);
                    b.SetInput(1, current[i + 1], true);
                    next.Add(Track(b.Output));
                }
                else
                {
                    next.Add(current[i]);
                }
            }
            current = next;
        }
        return current[0];
    }

    /// <summary>現在の入力を整数ピクセル境界で劣化なくビットマップに焼き付けて履歴スロットに保存する。</summary>
    bool Capture(ID2D1DeviceContext dc, int frame, DrawDescription description)
    {
        try
        {
            var b = dc.GetImageLocalBounds(input!);
            float w = b.Right - b.Left;
            float h = b.Bottom - b.Top;
            if (!(w > 0 && h > 0) || w > 8192 || h > 8192)
            {
                b = dc.GetImageWorldBounds(input!);
                w = b.Right - b.Left;
                h = b.Bottom - b.Top;
            }
            if (!(w > 0 && h > 0) || w > 8192 || h > 8192) return false;

            // 整数ピクセル境界に外接拡張し、サブピクセルぼやけと1px欠けを完全に防止する
            float left = MathF.Floor(b.Left);
            float top = MathF.Floor(b.Top);
            float right = MathF.Ceiling(b.Right);
            float bottom = MathF.Ceiling(b.Bottom);
            int iw = Math.Clamp((int)(right - left), 1, 8192);
            int ih = Math.Clamp((int)(bottom - top), 1, 8192);
            var alignedRect = new Vortice.RawRectF(left, top, left + iw, top + ih);

            if (captureDc is null)
            {
                using var device = dc.Device;
                captureDc = device.CreateDeviceContext(DeviceContextOptions.None);
            }
            var cdc = captureDc;
            cdc.UnitMode = UnitMode.Dips;
            cdc.Dpi = new(96, 96);

            var slot = slots[Mod(frame)];
            slot.Frame = int.MinValue;
            if (slot.Bitmap is not null && (slot.Width != iw || slot.Height != ih))
            {
                ReleaseSlotBitmap(slot, allowPool: true);
            }

            if (slot.Bitmap is null)
            {
                slot.Bitmap = AcquireBitmap(cdc, iw, ih, frame);
                if (slot.Bitmap is null) return false;
                slot.Width = iw;
                slot.Height = ih;
            }

            using var oldTarget = cdc.Target;
            try
            {
                cdc.Target = slot.Bitmap;
                cdc.BeginDraw();
                cdc.Clear(null);
                // 整数ピクセル境界の等倍コピーなので NearestNeighbor で劣化ゼロ
                cdc.DrawImage(input!, Vector2.Zero, alignedRect, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                cdc.EndDraw().CheckError();
            }
            finally
            {
                cdc.Target = oldTarget;
            }

            slot.Frame = frame;
            slot.Left = left;
            slot.Top = top;
            slot.Transform = new SlotTransform(description);
            slot.Placement = slot.Transform.ToMatrix();
            return true;
        }
        catch
        {
            return false;
        }
    }

    ID2D1Bitmap1? AcquireBitmap(ID2D1DeviceContext cdc, int width, int height, int currentFrame)
    {
        // プールに同サイズのビットマップがあれば即座に再利用
        for (int i = bitmapPool.Count - 1; i >= 0; i--)
        {
            if (bitmapPool[i].Width == width && bitmapPool[i].Height == height)
            {
                var reused = bitmapPool[i].Bitmap;
                bitmapPool.RemoveAt(i);
                return reused;
            }
        }

        long newBytes = (long)width * height * 4L;
        EnsureVramBudget(newBytes, currentFrame);

        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96, 96,
            BitmapOptions.Target);

        try
        {
            var bmp = cdc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, props);
            totalVramBytes += newBytes;
            return bmp;
        }
        catch
        {
            // VRAM不足時はプール全解放＋古い履歴を半分解放してリトライ
            DisposePool();
            EvictOldestSlots(currentFrame, MaxVramBytes / 2);
            try
            {
                var bmp = cdc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, props);
                totalVramBytes += newBytes;
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    void EnsureVramBudget(long incomingBytes, int currentFrame)
    {
        if (totalVramBytes + incomingBytes <= MaxVramBytes) return;

        // まずプール内の未使用ビットマップを解放
        while (bitmapPool.Count > 0 && totalVramBytes + incomingBytes > MaxVramBytes)
        {
            var last = bitmapPool[^1];
            bitmapPool.RemoveAt(bitmapPool.Count - 1);
            totalVramBytes -= last.Bytes;
            last.Bitmap.Dispose();
        }

        if (totalVramBytes + incomingBytes <= MaxVramBytes) return;

        // それでも超過する場合は最も古いスロットから順に解放
        EvictOldestSlots(currentFrame, MaxVramBytes - incomingBytes);
    }

    void EvictOldestSlots(int currentFrame, long targetBytes)
    {
        while (totalVramBytes > targetBytes)
        {
            Slot? oldest = null;
            int maxAge = -1;
            foreach (var s in slots)
            {
                if (s.Bitmap is null) continue;
                int age = s.Frame == int.MinValue ? int.MaxValue : Math.Abs(currentFrame - s.Frame);
                if (age > maxAge)
                {
                    maxAge = age;
                    oldest = s;
                }
            }
            if (oldest is null) break;
            ReleaseSlotBitmap(oldest, allowPool: false);
        }
    }

    void ReclaimUnusedSlots(int currentFrame, int keepSpan)
    {
        int minKeepFrame = currentFrame - keepSpan;
        foreach (var s in slots)
        {
            if (s.Bitmap is null) continue;
            if (s.Frame == int.MinValue || s.Frame < minKeepFrame || s.Frame > currentFrame)
            {
                ReleaseSlotBitmap(s, allowPool: true);
            }
        }
    }

    void ReleaseSlotBitmap(Slot slot, bool allowPool)
    {
        if (slot.Bitmap is null) return;
        var bmp = slot.Bitmap;
        int w = slot.Width;
        int h = slot.Height;
        long bytes = slot.Bytes;

        slot.Bitmap = null;
        slot.Frame = int.MinValue;
        slot.Width = 0;
        slot.Height = 0;

        if (allowPool && bitmapPool.Count < MaxPoolCount && totalVramBytes <= MaxVramBytes)
        {
            bitmapPool.Add(new PooledBitmap(bmp, w, h));
        }
        else
        {
            totalVramBytes -= bytes;
            bmp.Dispose();
        }
    }

    void InvalidateFutureSlots(int currentHistoryFrame)
    {
        foreach (var s in slots)
        {
            if (s.Frame > currentHistoryFrame)
            {
                s.Frame = int.MinValue;
            }
        }
    }

    static int Mod(int frame) => ((frame % MaxHistory) + MaxHistory) % MaxHistory;

    T Track<T>(T effect) where T : class
    {
        chain.Add(effect);
        return effect;
    }

    void ReleaseChain()
    {
        foreach (var e in chain)
        {
            if (e is IDisposable d)
                d.Dispose();
        }
        chain.Clear();
    }

    void DisposeSlots()
    {
        foreach (var s in slots)
        {
            ReleaseSlotBitmap(s, allowPool: false);
        }
    }

    void DisposePool()
    {
        foreach (var p in bitmapPool)
        {
            totalVramBytes -= p.Bytes;
            p.Bitmap.Dispose();
        }
        bitmapPool.Clear();
    }

    public void Dispose()
    {
        ReleaseChain();
        passthrough.SetInput(0, transparent, true);
        output.Dispose();
        passthrough.Dispose();
        transparent.Dispose();
        DisposeSlots();
        DisposePool();
        captureDc?.Dispose();
        captureDc = null;
    }
}
