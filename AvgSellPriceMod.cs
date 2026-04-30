using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.External;

namespace AvgSellPrice.Server;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader + 1)]
public class AvgSellPriceMod(TraderPriceHttpListener httpListener) : IPreSptLoadModAsync
{
    public Task PreSptLoadAsync()
    {
        _ = httpListener;
        return Task.CompletedTask;
    }
}
