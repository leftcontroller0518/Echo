using System.ComponentModel.DataAnnotations;

namespace YMM4Echo;

/// <summary>After Effects「エコー演算子」に対応するブレンド方式。</summary>
public enum EchoOperator
{
    [Display(Name = "追加")]
    Add,

    [Display(Name = "最大")]
    Maximum,

    [Display(Name = "最小")]
    Minimum,

    [Display(Name = "スクリーン")]
    Screen,

    [Display(Name = "後方にコンポジット")]
    CompositeInBack,

    [Display(Name = "前方にコンポジット")]
    CompositeInFront,

    [Display(Name = "ブレンド")]
    Blend,
}
