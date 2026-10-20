using System.Windows;
using System.Windows.Controls;

namespace AzerothManager.Views;

/// <summary>
/// PasswordBox n'expose pas Password en propriété de dépendance, volontairement : WPF ne
/// veut pas qu'un mot de passe traîne dans l'arbre des liaisons. Mais les secrets du profil
/// vivent déjà en clair dans <see cref="Models.ServerProfile"/>, en mémoire seulement, et
/// le chiffrement DPAPI a lieu à l'enregistrement — le pont ne dégrade donc rien, et il
/// évite qu'un mot de passe s'affiche en clair à l'écran (§21).
///
/// Même construction que <see cref="AvalonEditBehaviour"/> : valeur par défaut null, pour
/// qu'une chaîne vide initiale déclenche malgré tout le rappel et l'abonnement.
/// </summary>
public static class PasswordBoxBehaviour
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword", typeof(string), typeof(PasswordBoxBehaviour),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static string? GetBoundPassword(DependencyObject o) => (string?)o.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject o, string? value) => o.SetValue(BoundPasswordProperty, value);

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked", typeof(bool), typeof(PasswordBoxBehaviour), new PropertyMetadata(false));

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        if (!(bool)box.GetValue(IsHookedProperty))
        {
            box.SetValue(IsHookedProperty, true);
            box.PasswordChanged += (s, _) =>
            {
                if (s is PasswordBox pb) SetBoundPassword(pb, pb.Password);
            };
        }

        // Réécrire Password à l'identique replacerait le curseur au début pendant la saisie.
        var text = e.NewValue as string ?? "";
        if (box.Password != text) box.Password = text;
    }
}
