using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;
using System.Linq;
using System.Threading;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace AvgSellPrice.Server;

[Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.PreSptModLoader - 1)]
public class TraderPriceService(
    ISptLogger<TraderPriceService> logger,
    DatabaseService databaseService,
    RagfairOfferHolder ragfairOfferHolder)
{
    private Dictionary<string, int> _traderCache = [];
    private Dictionary<string, FleaPriceEntry> _fleaCache = [];
    private Dictionary<string, List<string>> _weaponDefaultPresetCache = [];
    private bool _traderInitialized = false;
    private bool _fleaInitialized = false;
    private bool _weaponDefaultPresetsInitialized = false;
    private readonly Lock _lock = new();

    private const string RubleTemplateId = "5449016a4bdc2d6f028b456f";
    private const string EuroTemplateId = "569668774bdc2da2298b4568";
    private const string DollarTemplateId = "5696686a4bdc2da3298b456a";
    private const double EuroToRoubles = 145d;
    private const double DollarToRoubles = 130d;
    private const int MinimumLiveFleaPrice = 100;
    private const double MinimumLiveToStaticPriceRatio = 0.1d;

    public IReadOnlyDictionary<string, int> GetTraderBuyPrices()
    {
        if (_traderInitialized)
        {
            return _traderCache;
        }

        lock (_lock)
        {
            if (_traderInitialized)
            {
                return _traderCache;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var tables = databaseService.GetTables();
                var traders = tables?.Traders;

                if (traders == null)
                {
                    logger.Warning("[AvgSellPrice] No traders found in database");
                    _traderInitialized = true;
                    return _traderCache;
                }

                var bestPrices = new Dictionary<string, int>();

                foreach (var (_, trader) in traders)
                {
                    if (trader?.Base?.AvailableInRaid == true)
                    {
                        continue;
                    }

                    var assort = trader?.Assort;
                    if (assort?.Items == null || assort.BarterScheme == null)
                    {
                        continue;
                    }

                    foreach (var item in assort.Items)
                    {
                        if (item == null || item.ParentId != "hideout")
                        {
                            continue;
                        }

                        string tpl = item.Template.ToString();
                        if (string.IsNullOrEmpty(tpl))
                        {
                            continue;
                        }

                        string itemId = item.Id.ToString();
                        if (string.IsNullOrEmpty(itemId))
                        {
                            continue;
                        }

                        if (!assort.BarterScheme.TryGetValue(item.Id, out var schemes))
                        {
                            continue;
                        }

                        if (schemes == null || schemes.Count == 0)
                        {
                            continue;
                        }

                        var scheme = schemes[0];
                        if (scheme == null || scheme.Count == 0)
                        {
                            continue;
                        }

                        var req = scheme[0];
                        if (req == null)
                        {
                            continue;
                        }

                        string requirementTemplate = req.Template.ToString();
                        if (requirementTemplate != RubleTemplateId)
                        {
                            continue;
                        }

                        int price = (int)(req.Count ?? 0);
                        if (price <= 0)
                        {
                            continue;
                        }

                        if (!bestPrices.TryGetValue(tpl, out int existing) || price > existing)
                        {
                            bestPrices[tpl] = price;
                        }
                    }
                }

                _traderCache = bestPrices;
                _traderInitialized = true;

                stopwatch.Stop();
                logger.Info($"[AvgSellPrice] Built trader buy price map: {_traderCache.Count} items in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                logger.Error($"[AvgSellPrice] Error building price map: {ex}");
                _traderInitialized = true;
            }

            return _traderCache;
        }
    }

    public IReadOnlyDictionary<string, FleaPriceEntry> GetFleaPrices()
    {
        EnsureStaticFleaPricesInitialized();

        var fleaPrices = new Dictionary<string, FleaPriceEntry>(_fleaCache);
        object? templates = null;
        try
        {
            var tables = databaseService.GetTables();
            templates = tables?.GetType().GetProperty("Templates")?.GetValue(tables);
        }
        catch (Exception ex)
        {
            logger.Warning($"[AvgSellPrice] Could not read flea sellable map for live offers: {ex.Message}");
        }

        AddLiveFleaOfferPrices(fleaPrices, GetFleaSellableMap(templates));
        return fleaPrices;
    }

    public IReadOnlyDictionary<string, List<string>> GetWeaponDefaultPresets()
    {
        if (_weaponDefaultPresetsInitialized)
        {
            return _weaponDefaultPresetCache;
        }

        lock (_lock)
        {
            if (_weaponDefaultPresetsInitialized)
            {
                return _weaponDefaultPresetCache;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var tables = databaseService.GetTables();
                object? globals = tables?.GetType().GetProperty("Globals")?.GetValue(tables);
                object? itemPresetsObject = globals != null ? GetMemberValue(globals, "ItemPresets") : null;

                var bestByWeapon = new Dictionary<string, PresetCandidate>();

                if (itemPresetsObject is IDictionary presets)
                {
                    foreach (DictionaryEntry entry in presets)
                    {
                        object? preset = entry.Value;
                        object? itemsObject = preset != null ? GetMemberValue(preset, "Items", "_items") : null;
                        if (itemsObject is not IEnumerable enumerable)
                        {
                            continue;
                        }

                        var items = enumerable.Cast<object?>()
                            .Where(x => x != null)
                            .ToList();

                        if (items.Count <= 1)
                        {
                            continue;
                        }

                        object? root = items.FirstOrDefault(x => string.IsNullOrWhiteSpace(GetStringMember(x!, "ParentId", "parentId")));
                        if (root == null)
                        {
                            continue;
                        }

                        string? weaponTemplateId = GetStringMember(root, "Template", "_tpl", "Tpl");
                        if (string.IsNullOrWhiteSpace(weaponTemplateId))
                        {
                            continue;
                        }

                        var defaultParts = items
                            .Where(x => !ReferenceEquals(x, root))
                            .Where(x => !IsAmmoSlot(GetStringMember(x!, "SlotId", "slotId")))
                            .Select(x => GetStringMember(x!, "Template", "_tpl", "Tpl"))
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct()
                            .ToList();

                        if (defaultParts.Count == 0)
                        {
                            continue;
                        }

                        int presetSize = defaultParts.Count;
                        if (!bestByWeapon.TryGetValue(weaponTemplateId, out PresetCandidate? existing) ||
                            presetSize < existing.Size)
                        {
                            bestByWeapon[weaponTemplateId] = new PresetCandidate(presetSize, defaultParts!);
                        }
                    }
                }

                _weaponDefaultPresetCache = bestByWeapon.ToDictionary(
                    x => x.Key,
                    x => x.Value.Parts);

                _weaponDefaultPresetsInitialized = true;

                stopwatch.Stop();
                logger.Info($"[AvgSellPrice] Built weapon default preset map: {_weaponDefaultPresetCache.Count} weapons in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                logger.Error($"[AvgSellPrice] Error building weapon default preset map: {ex}");
                _weaponDefaultPresetsInitialized = true;
            }

            return _weaponDefaultPresetCache;
        }
    }

    private void EnsureStaticFleaPricesInitialized()
    {
        if (_fleaInitialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_fleaInitialized)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var tables = databaseService.GetTables();
                object? templates = tables?.GetType().GetProperty("Templates")?.GetValue(tables);
                object? pricesObject = templates?.GetType().GetProperty("Prices")?.GetValue(templates);

                var fleaPrices = new Dictionary<string, FleaPriceEntry>();
                var fleaSellable = GetFleaSellableMap(templates);

                if (pricesObject is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        string? templateId = entry.Key?.ToString();
                        if (string.IsNullOrWhiteSpace(templateId))
                        {
                            continue;
                        }

                        int price = Convert.ToInt32(entry.Value ?? 0);
                        if (price <= 0)
                        {
                            continue;
                        }

                        if (fleaPrices.ContainsKey(templateId))
                        {
                            continue;
                        }

                        bool sellable = !fleaSellable.TryGetValue(templateId, out bool canSell) || canSell;
                        fleaPrices[templateId] = new FleaPriceEntry(price, sellable);
                    }
                }
                else
                {
                    logger.Warning("[AvgSellPrice] No flea price table found in templates");
                }

                _fleaCache = fleaPrices;
                _fleaInitialized = true;

                stopwatch.Stop();
                logger.Info($"[AvgSellPrice] Built static flea price map: {_fleaCache.Count} items in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                logger.Error($"[AvgSellPrice] Error building flea price map: {ex}");
                _fleaInitialized = true;
            }
        }
    }

    private void AddLiveFleaOfferPrices(
        Dictionary<string, FleaPriceEntry> fleaPrices,
        Dictionary<string, bool> fleaSellable)
    {
        List<RagfairOffer>? offers = null;

        try
        {
            offers = ragfairOfferHolder.GetOffers();
        }
        catch (Exception ex)
        {
            logger.Warning($"[AvgSellPrice] Could not read live flea offers, using static prices only: {ex.Message}");
        }

        if (offers == null || offers.Count == 0)
        {
            return;
        }

        var offerPricesByTemplate = new Dictionary<string, List<int>>();

        foreach (RagfairOffer offer in offers)
        {
            if (offer?.Items == null || offer.Items.Count == 0)
            {
                continue;
            }

            Item? rootItem = offer.Items.FirstOrDefault(x => x.Id == offer.Root) ?? offer.Items[0];
            if (rootItem == null)
            {
                continue;
            }

            string? templateId = rootItem.Template.ToString();
            if (string.IsNullOrWhiteSpace(templateId))
            {
                continue;
            }

            bool sellable = !fleaSellable.TryGetValue(templateId, out bool canSell) || canSell;
            if (!sellable)
            {
                continue;
            }

            if (!IsFullDurabilityOffer(rootItem))
            {
                continue;
            }

            int price = GetOfferRoublePricePerItem(offer, rootItem);
            if (price <= 0)
            {
                continue;
            }

            if (!offerPricesByTemplate.TryGetValue(templateId, out List<int>? prices))
            {
                prices = [];
                offerPricesByTemplate[templateId] = prices;
            }

            prices.Add(price);
        }

        foreach (var (templateId, prices) in offerPricesByTemplate)
        {
            if (prices.Count == 0)
            {
                continue;
            }

            int staticPrice = fleaPrices.TryGetValue(templateId, out FleaPriceEntry? existing)
                ? existing.Price
                : 0;

            int minimumPrice = staticPrice > 0
                ? Math.Max(MinimumLiveFleaPrice, (int)Math.Floor(staticPrice * MinimumLiveToStaticPriceRatio))
                : MinimumLiveFleaPrice;

            int[] cheapestValidPrices = prices
                .Where(price => price >= minimumPrice)
                .OrderBy(price => price)
                .Take(3)
                .ToArray();

            if (cheapestValidPrices.Length == 0)
            {
                continue;
            }

            int averagedPrice = (int)Math.Ceiling(cheapestValidPrices.Average());
            if (averagedPrice > 0)
            {
                fleaPrices[templateId] = new FleaPriceEntry(averagedPrice, true);
            }
        }
    }

    private static bool IsFullDurabilityOffer(Item item)
    {
        UpdRepairable? repairable = item?.Upd?.Repairable;
        if (repairable?.Durability == null || repairable.MaxDurability == null)
        {
            return true;
        }

        double max = repairable.MaxDurability.Value;
        if (max <= 0d)
        {
            return true;
        }

        return repairable.Durability.Value >= max - 0.01d;
    }

    private static int GetOfferRoublePricePerItem(RagfairOffer offer, Item rootItem)
    {
        if (offer.Requirements == null)
        {
            return 0;
        }

        double total = 0d;

        foreach (OfferRequirement requirement in offer.Requirements)
        {
            if (requirement?.Count == null)
            {
                continue;
            }

            double rate = GetCurrencyRate(requirement.TemplateId.ToString());
            if (rate <= 0d)
            {
                return 0;
            }

            total += requirement.Count.Value * rate;
        }

        if (total <= 0d)
        {
            return 0;
        }

        double stackCount = rootItem.Upd?.StackObjectsCount ?? 1d;
        if (stackCount <= 0d)
        {
            stackCount = 1d;
        }

        return (int)Math.Ceiling(total / stackCount);
    }

    private static double GetCurrencyRate(string? templateId)
    {
        return templateId switch
        {
            RubleTemplateId => 1d,
            DollarTemplateId => DollarToRoubles,
            EuroTemplateId => EuroToRoubles,
            _ => 0d
        };
    }

    private static bool IsAmmoSlot(string? slotId)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            return false;
        }

        string lower = slotId.ToLowerInvariant();
        return lower.Contains("patron") ||
               lower.Contains("chamber") ||
               lower.Contains("cartridge");
    }

    private Dictionary<string, bool> GetFleaSellableMap(object? templates)
    {
        var result = new Dictionary<string, bool>();

        object? itemsObject = templates?.GetType().GetProperty("Items")?.GetValue(templates);
        if (itemsObject is not IDictionary items)
        {
            return result;
        }

        foreach (DictionaryEntry entry in items)
        {
            string? templateId = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(templateId) || entry.Value == null)
            {
                continue;
            }

            if (TryGetCanSellOnRagfair(entry.Value, out bool canSell))
            {
                result[templateId] = canSell;
            }
        }

        return result;
    }

    private static bool TryGetCanSellOnRagfair(object template, out bool canSell)
    {
        canSell = true;

        if (TryReadBool(template, "CanSellOnRagfair", out canSell))
        {
            return true;
        }

        object? props = GetMemberValue(template, "Props", "_props");
        if (props == null)
        {
            return false;
        }

        return TryReadBool(props, "CanSellOnRagfair", out canSell);
    }

    private static bool TryReadBool(object source, string memberName, out bool value)
    {
        value = false;

        Type? type = source.GetType();
        while (type != null)
        {
            var property = type.GetProperty(memberName);
            if (property != null)
            {
                object? raw = property.GetValue(source);
                if (raw is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }
            }

            var field = type.GetField(memberName);
            if (field != null)
            {
                object? raw = field.GetValue(source);
                if (raw is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }
            }

            type = type.BaseType;
        }

        return false;
    }

    private static object? GetMemberValue(object source, params string[] memberNames)
    {
        Type? type = source.GetType();
        while (type != null)
        {
            foreach (string memberName in memberNames)
            {
                var property = type.GetProperty(memberName);
                if (property != null)
                {
                    return property.GetValue(source);
                }

                var field = type.GetField(memberName);
                if (field != null)
                {
                    return field.GetValue(source);
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static string? GetStringMember(object source, params string[] memberNames)
    {
        return GetMemberValue(source, memberNames)?.ToString();
    }

    private record PresetCandidate(int Size, List<string> Parts);
}

public record FleaPriceEntry(int Price, bool Sellable);
