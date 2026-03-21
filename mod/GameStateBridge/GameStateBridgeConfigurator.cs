using Bindito.Core;

namespace GameStateBridge
{
    [Context("Game")]
    public class GameStateBridgeConfigurator : Configurator
    {
        public override void Configure()
        {
            Bind<GameStateBridgeService>().AsSingleton();
        }
    }
}
