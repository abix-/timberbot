// TimberbotConfigurator.cs -- Bindito DI registration.
//
// Timberborn uses Bindito (a custom DI framework) to wire game services.
// [Context("Game")] means this runs when a game is loaded (not on the main menu).

using Bindito.Core;

namespace Timberbot
{
    [Context("Game")]
    public class TimberbotConfigurator : Configurator
    {
        public override void Configure()
        {
            Bind<TimberbotEntityCache>().AsSingleton();
            Bind<TimberbotWebhook>().AsSingleton();
            Bind<TimberbotRead>().AsSingleton();
            Bind<TimberbotWrite>().AsSingleton();
            Bind<TimberbotPlacement>().AsSingleton();
            Bind<TimberbotDebug>().AsSingleton();
            Bind<TimberbotService>().AsSingleton();
        }
    }
}
