using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Player.Video;

namespace YMM4Echo;

/// <summary>
/// After Effects の「エコー」エフェクトを再現する映像エフェクト。
/// 現在のフレームと、過去のフレームを一定間隔で重ねて残像・筋・にじみを作る。
/// </summary>
[VideoEffect("エコー", ["時間"], ["echo", "エコー", "残像", "トレイル", "AfterEffects", "AE"], isAviUtlSupported: false)]
public class EchoEffect : VideoEffectBase
{
    public override string Label => "エコー";

    [Display(GroupName = "エコー", Name = "エコー時間(秒)",
        Description = "エコー同士の間隔（秒）。マイナスで過去のフレームからエコーを作ります。\nプラス（未来側）はリアルタイム処理の制約上、過去側と同じ扱いになります。")]
    [AnimationSlider("F3", "秒", -1, 1)]
    public Animation EchoTime { get; } = new Animation(-0.033, -10, 10);

    [Display(GroupName = "エコー", Name = "エコーの数",
        Description = "エコーさせる回数（枚数）。1なら 現在＋1つ前 の2枚を合成します。")]
    [AnimationSlider("F0", "個", 0, 100)]
    public Animation EchoCount { get; } = new Animation(1, 0, 1000);

    [Display(GroupName = "エコー", Name = "開始強度",
        Description = "最初のイメージ（現在のフレーム）の不透明度。")]
    [AnimationSlider("F2", "", 0, 2)]
    public Animation StartIntensity { get; } = new Animation(1, 0, 10);

    [Display(GroupName = "エコー", Name = "減衰",
        Description = "直前のエコーに対する、次のエコーの不透明度の割合。1.0で減衰なし。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation Decay { get; } = new Animation(1.0, 0, 10);

    [Display(GroupName = "エコー", Name = "エコー演算子",
        Description = "エコーを組み合わせるブレンド方式。")]
    [EnumComboBox]
    public EchoOperator Operator { get => op; set => Set(ref op, value); }
    EchoOperator op = EchoOperator.CompositeInBack;

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new EchoEffectProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables() => [EchoTime, EchoCount, StartIntensity, Decay];
}
