using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers.Http;

namespace AvgSellPrice.Server;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader)]
public class TraderPriceHttpListener(
    ISptLogger<TraderPriceHttpListener> logger,
    TraderPriceService traderPriceService) : IHttpListener
{
    private const string RoutePrefix = "/AvgSellPrice";
    private const string RouteGetPrices = "/AvgSellPrice/traderBuyPrices";
    private const string RouteGetFleaPrices = "/AvgSellPrice/fleaPrices";
    private const string RouteGetWeaponDefaultPresets = "/AvgSellPrice/weaponDefaultPresets";

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        return context.Request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        try
        {
            if (context.Request.Path.Equals(RouteGetPrices, StringComparison.OrdinalIgnoreCase))
            {
                var prices = traderPriceService.GetTraderBuyPrices();
                await context.Response.WriteAsJsonAsync(prices, context.RequestAborted);
            }
            else if (context.Request.Path.Equals(RouteGetFleaPrices, StringComparison.OrdinalIgnoreCase))
            {
                var prices = traderPriceService.GetFleaPrices();
                await context.Response.WriteAsJsonAsync(prices, context.RequestAborted);
            }
            else if (context.Request.Path.Equals(RouteGetWeaponDefaultPresets, StringComparison.OrdinalIgnoreCase))
            {
                var presets = traderPriceService.GetWeaponDefaultPresets();
                await context.Response.WriteAsJsonAsync(presets, context.RequestAborted);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[AvgSellPrice] Error handling request: {ex.Message}");
            context.Response.StatusCode = 500;
        }
    }
}
