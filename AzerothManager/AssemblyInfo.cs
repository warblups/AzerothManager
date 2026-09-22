using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Le projet de tests atteint les membres internes — ceux qui restent des détails
// d'implémentation plutôt que des services : l'analyse de la sortie d'une commande GM, par
// exemple, n'a pas à être publique pour être vérifiable.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AzerothManager.Tests")]
