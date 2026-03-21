using Bindito.Core;

namespace Timberbot
{
    [Context("Game")]
    public class TimberbotConfigurator : Configurator
    {
        public override void Configure()
        {
            Bind<TimberbotService>().AsSingleton();
        }
    }
}
