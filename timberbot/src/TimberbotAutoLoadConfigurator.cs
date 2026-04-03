// TimberbotAutoLoadConfigurator.cs. MainMenu DI registration for auto-load.
//
// [Context("MainMenu")] means this runs at the main menu, before any game is loaded.
// Registers TimberbotAutoLoad which checks CLI args and triggers save loading.

using Bindito.Core;

namespace Timberbot
{
    [Context("MainMenu")]
    public class TimberbotAutoLoadConfigurator : Configurator
    {
        public override void Configure()
        {
            Bind<TimberbotAutoLoad>().AsSingleton();
        }
    }
}
