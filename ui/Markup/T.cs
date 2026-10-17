using Microsoft.UI.Xaml.Markup;

namespace ViaSonic.Markup;

/// <summary>XAML 里取翻译：{l:T Text='中文'}。页面创建时取值，切换语言后靠重新加载页面刷新。</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class T : MarkupExtension
{
    public string Text { get; set; } = "";

    protected override object ProvideValue() => Lang.T(Text);
}
