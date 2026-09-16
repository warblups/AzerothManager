using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace AzerothManager.Views;

/// <summary>
/// AvalonEdit expose Text comme une propriété simple, non liable en deux sens.
/// Cette propriété attachée fait le pont avec le ViewModel, et applique au passage
/// la coloration syntaxique SQL (§15).
/// </summary>
public static class AvalonEditBehaviour
{
    public static readonly DependencyProperty BindableTextProperty =
        DependencyProperty.RegisterAttached(
            "BindableText", typeof(string), typeof(AvalonEditBehaviour),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBindableTextChanged));

    public static string GetBindableText(DependencyObject o) => (string)o.GetValue(BindableTextProperty);
    public static void SetBindableText(DependencyObject o, string value) => o.SetValue(BindableTextProperty, value);

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;

        if (editor.Tag is not bool)
        {
            editor.Tag = true;
            editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            editor.TextChanged += (s, _) =>
            {
                if (s is TextEditor te) SetBindableText(te, te.Text);
            };
        }

        var text = e.NewValue as string ?? "";
        if (editor.Text != text) editor.Text = text;
    }
}
