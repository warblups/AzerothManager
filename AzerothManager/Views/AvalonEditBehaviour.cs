using System.Windows;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace AzerothManager.Views;

/// <summary>
/// AvalonEdit expose Text comme une propriété simple, non liable en deux sens.
/// Cette propriété attachée fait le pont avec le ViewModel, et applique au passage
/// la coloration syntaxique SQL (§15).
///
/// La valeur par défaut est null, et non la chaîne vide : le ViewModel initialise
/// SqlText à "", et une valeur par défaut identique ne produirait aucun changement,
/// donc aucun appel du rappel — l'abonnement à TextChanged n'aurait jamais lieu et
/// la saisie n'atteindrait jamais le ViewModel.
/// </summary>
public static class AvalonEditBehaviour
{
    public static readonly DependencyProperty BindableTextProperty =
        DependencyProperty.RegisterAttached(
            "BindableText", typeof(string), typeof(AvalonEditBehaviour),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBindableTextChanged));

    public static string? GetBindableText(DependencyObject o) => (string?)o.GetValue(BindableTextProperty);
    public static void SetBindableText(DependencyObject o, string? value) => o.SetValue(BindableTextProperty, value);

    /// <summary>Marque un éditeur déjà abonné, plutôt que de détourner Tag qui appartient à l'auteur de la vue.</summary>
    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked", typeof(bool), typeof(AvalonEditBehaviour), new PropertyMetadata(false));

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;

        Hook(editor);

        var text = e.NewValue as string ?? "";
        if (editor.Text != text) editor.Text = text;
    }

    /// <summary>
    /// Coloration SQL pour fond sombre, chargée une fois. La définition TSQL fournie
    /// par AvalonEdit vise un fond blanc et devient illisible ici.
    /// </summary>
    private static readonly Lazy<IHighlightingDefinition?> SqlDarkHighlighting = new(() =>
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Themes/SqlDark.xshd", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri)!.Stream;
            using var reader = new XmlTextReader(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            // Plutôt aucune coloration qu'une coloration illisible.
            return null;
        }
    });

    private static void Hook(TextEditor editor)
    {
        if ((bool)editor.GetValue(IsHookedProperty)) return;
        editor.SetValue(IsHookedProperty, true);

        editor.SyntaxHighlighting = SqlDarkHighlighting.Value;
        editor.TextChanged += (s, _) =>
        {
            if (s is TextEditor te) SetBindableText(te, te.Text);
        };

        // Filet de sécurité : si la liaison n'a encore rien émis au chargement,
        // l'abonnement est en place malgré tout.
        editor.Loaded += (s, _) =>
        {
            if (s is TextEditor te) Hook(te);
        };
    }
}
