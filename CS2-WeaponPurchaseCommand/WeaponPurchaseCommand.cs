using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using static CounterStrikeSharp.API.Core.Listeners;

namespace CS2_WeaponPurchaseCommand;

public class WeaponsPurchase(ILogger<WeaponsPurchase> logger) : BasePlugin
{
    public override string ModuleName => "CS2-WeaponPurchaseCommand";
    public override string ModuleDescription => "A plugin to purchase weapons in CS2 Zombie Escape";
    public override string ModuleAuthor => "Oylsister";
    public override string ModuleVersion => "2.0";

    private readonly ILogger<WeaponsPurchase> _logger = logger;
    public static Dictionary<string, WeaponAttribute>? WeaponsConfig = null;
    bool weaponCommandInitialized = false;
    public string ConfigPath = Path.Combine(Application.RootDirectory, "configs/zombiesharp/");
    public FakeConVar<string> WeaponConfigPath = new("css_weapon_config_path", "Weapon Config Path", "weapons.jsonc", ConVarFlags.FCVAR_NONE);
    public Dictionary<CCSPlayerController, PurchaseCount>? PlayerPurchaseCount { get; set; } = [];

    public override void Load(bool hotReload)
    {
        AddCommand("zs_restrict", "Restrict Weapon Command", WeaponRestrictCommand);
        AddCommand("zs_unrestrict", "Unrestrict Weapon Command", WeaponUnrestrictCommand);

        RegisterListener<OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<OnMapStart>(OnMapStart);

        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<OnClientPutInServer>(OnClientPutInServer);
        RemoveListener<OnClientDisconnect>(OnClientDisconnect);
        RemoveListener<OnMapStart>(OnMapStart);

        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre);
    }

    public void OnClientPutInServer(int playerSlot)
    {
        var client = Utilities.GetPlayerFromSlot(playerSlot);

        if (client == null || !client.IsValid)
            return;

        // we need to make sure this is not null.
        if (PlayerPurchaseCount == null)
            PlayerPurchaseCount = [];

        // if player purchase count is not in the dictionary, add it.
        if (!PlayerPurchaseCount.ContainsKey(client))
            PlayerPurchaseCount.Add(client, new PurchaseCount());
    }

    public void OnClientDisconnect(int playerSlot)
    {
        var client = Utilities.GetPlayerFromSlot(playerSlot);

        if (client == null || !client.IsValid)
            return;

        PlayerPurchaseCount?.Remove(client);
    }

    public void OnMapStart(string mapName)
    {
        PlayerPurchaseCount?.Clear();
        WeaponsConfig?.Clear();

        // make sure this one is null.
        WeaponsConfig = null;

        // initial weapon config data
        WeaponsConfig = new Dictionary<string, WeaponAttribute>();
        var configPath = Path.Combine(ConfigPath, WeaponConfigPath.Value ?? "weapons.jsonc");

        if (!File.Exists(configPath))
        {
            _logger.LogCritical("[WeaponsOnMapStart] Couldn't find a {path} file!", configPath);
            return;
        }

        _logger.LogInformation("[WeaponsOnMapStart] Load Weapon Config file {path}.", configPath);

        // we get data from jsonc file.
        WeaponsConfig = JsonConvert.DeserializeObject<Dictionary<string, WeaponAttribute>>(File.ReadAllText(configPath));

        // we create weapon purchase command here.
        IntialWeaponPurchaseCommand();
    }

    public HookResult OnCanAcquire(DynamicHook hook)
    {
        var itemService = hook.GetParam<CCSPlayer_ItemServices>(0);
        var weapon = VirtualFunctions.GetCSWeaponDataFromKey(-1, hook.GetParam<CEconItemView>(1).ItemDefinitionIndex.ToString());
        var method = hook.GetParam<AcquireMethod>(2);
        var client = itemService.Pawn.Value.Controller.Value?.As<CCSPlayerController>();

        if(client == null)
            return HookResult.Continue;

        if (client.Team == CsTeam.Terrorist && method == AcquireMethod.Buy)
        {
            client.PrintToChat($" {Localizer["Prefix"]} You can't purchase as Zombie.");
            hook.SetReturn(AcquireResult.NotAllowedByProhibition);
            return HookResult.Handled;
        }

        // if player buy from menu tell them they can't.
        if (IsRestricted(weapon.Name))
        {
            var attribute = GetWeaponAttributeByEntityName(weapon.Name);

            if (method == AcquireMethod.Buy)
                client.PrintToChat($" {Localizer["Prefix"]} {Localizer["Weapon.IsRestricted", attribute?.WeaponName!]}");

            hook.SetReturn(AcquireResult.NotAllowedByProhibition);
            return HookResult.Handled;
        }

        else
        {
            if (method == AcquireMethod.Buy)
            {
                var attribute = GetWeaponAttributeByEntityName(weapon.Name);

                if (attribute != null)
                {
                    PurchaseWeapon(client, attribute);
                    hook.SetReturn(AcquireResult.NotAllowedByProhibition);
                    return HookResult.Handled;
                }
            }
        }

        return HookResult.Continue;
    }

    public void IntialWeaponPurchaseCommand()
    {
        // if this part has been done before don't do it again.
        if (weaponCommandInitialized)
            return;

        // safety first.
        if (WeaponsConfig == null)
        {
            _logger.LogError("[IntialWeaponPurchaseCommand] Weapon Configs is null!");
            return;
        }

        foreach (var weapon in WeaponsConfig.Values)
        {
            if (weapon.PurchaseCommand == null || weapon.PurchaseCommand.Count <= 0)
                continue;

            foreach (var command in weapon.PurchaseCommand)
            {
                if (string.IsNullOrEmpty(command))
                    continue;

                AddCommand(command, $"Weapon {weapon.WeaponName} Purchase Command", WeaponPurchaseCommand);
            }
        }

        weaponCommandInitialized = true;
    }

    [RequiresPermissions("@css/slay")]
    public void WeaponRestrictCommand(CCSPlayerController? client, CommandInfo info)
    {
        if (WeaponsConfig == null)
        {
            _logger.LogError("[WeaponRestrictCommand] WeaponsConfig is null!");
            return;
        }

        if (info.ArgCount > 1)
        {
            var weaponname = info.GetArg(1);
            var weapon = GetWeaponAttributeByName(weaponname);

            if (weapon == null)
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Weapon.NotFound", weaponname]}");
                return;
            }

            Server.PrintToChatAll($" {Localizer["Prefix"]} {Localizer["Restrict.Weapon", weapon.WeaponName!]}");
            weapon.Restrict = true;
            return;
        }

        if (client == null)
            return;

        var menu = new ChatMenu($" {Localizer["Prefix"]} {Localizer["Restrict.MainMenu"]}");

        foreach (var weapon in WeaponsConfig)
        {
            menu.AddMenuOption(weapon.Value.WeaponName!, (client, option) =>
            {
                weapon.Value.Restrict = true;
                Server.PrintToChatAll($" {Localizer["Prefix"]} {Localizer["Restrict.Weapon", weapon.Value.WeaponName!]}");
                MenuManager.CloseActiveMenu(client);
            },
            weapon.Value.Restrict);
        }
        menu.ExitButton = true;
        MenuManager.OpenChatMenu(client, menu);
    }

    [RequiresPermissions("@css/slay")]
    public void WeaponUnrestrictCommand(CCSPlayerController? client, CommandInfo info)
    {
        if (WeaponsConfig == null)
        {
            _logger.LogError("[WeaponRestrictCommand] WeaponsConfig is null!");
            return;
        }

        if (info.ArgCount > 1)
        {
            var weaponname = info.GetArg(1);
            var weapon = GetWeaponAttributeByName(weaponname);

            if (weapon == null)
            {
                info.ReplyToCommand($" {Localizer["Prefix"]} {Localizer["Weapon.NotFound"]}");
                return;
            }

            Server.PrintToChatAll($" {Localizer["Prefix"]} {Localizer["Unrestrict.Weapon", weapon.WeaponName!]}");
            weapon.Restrict = false;
            return;
        }

        if (client == null)
            return;

        var menu = new ChatMenu($" {Localizer["Prefix"]} {Localizer["Unrestrict.MainMenu"]}");

        foreach (var weapon in WeaponsConfig)
        {
            menu.AddMenuOption(weapon.Value.WeaponName!, (client, option) =>
            {
                weapon.Value.Restrict = false;
                Server.PrintToChatAll($" {Localizer["Prefix"]} {Localizer["Unrestrict.Weapon", weapon.Value.WeaponName!]}");
                MenuManager.CloseActiveMenu(client);
            },
            !weapon.Value.Restrict);
        }
        menu.ExitButton = true;
        MenuManager.OpenChatMenu(client, menu);
    }

    [CommandHelper(0, "", CommandUsage.CLIENT_ONLY)]
    public void WeaponPurchaseCommand(CCSPlayerController? client, CommandInfo info)
    {
        // args 0 is basically command string.
        var command = info.GetArg(0);
        var weaponAttribute = WeaponsConfig?.Where(weapon => weapon.Value.PurchaseCommand!.Contains(command)).FirstOrDefault().Value;

        if (weaponAttribute != null && client != null)
            PurchaseWeapon(client, weaponAttribute);
    }
    
    public void PurchaseWeapon(CCSPlayerController client, WeaponAttribute attribute)
    {
        // double check for possible
        if (attribute == null || client == null)
            return;

        // if not alive.
        if (!IsPlayerAlive(client))
        {
            client.PrintToChat($" {Localizer["Prefix"]} {Localizer["Core.MustBeAlive"]}");
            return;
        }

        if (client.Team == CsTeam.Terrorist)
        {
            client.PrintToChat($" {Localizer["Prefix"]} You can't purchase as Zombie.");
            return;
        }

        // if it's restricted
        if (IsRestricted(attribute.WeaponEntity!))
        {
            client.PrintToChat($" {Localizer["Prefix"]} {Localizer["Weapon.IsRestricted", attribute.WeaponName!]}");
            return;
        }

        // check their cash in account
        if (client.InGameMoneyServices?.Account < attribute.Price)
        {
            client.PrintToChat($" {Localizer["Prefix"]} {Localizer["Weapon.NotEnoughCash"]}");
            return;
        }

        // Max Purchase section, will be added later.
        if (attribute.MaxPurchase != 0)
        {
            var count = 0;

            if (PlayerPurchaseCount == null)
            {
                _logger.LogError("[PurchaseWeapon] Player Purchase count is null!");
                return;
            }

            if (!PlayerPurchaseCount.ContainsKey(client))
            {
                _logger.LogError("[PurchaseWeapon] Player {name} is not in purchase count data, so create a new one", client.PlayerName);
                PlayerPurchaseCount.Add(client, new());
            }

            if (PlayerPurchaseCount[client].WeaponCount == null)
            {
                _logger.LogError("[PurchaseWeapon] Player {name} Purchase data is null", client.PlayerName);
                return;
            }

            if (PlayerPurchaseCount[client].WeaponCount!.ContainsKey(attribute.WeaponEntity!))
                count = PlayerPurchaseCount[client].WeaponCount![attribute.WeaponEntity!];

            if (count >= attribute.MaxPurchase)
            {
                client.PrintToChat($" {Localizer["Prefix"]} {Localizer["Weapon.ReachMaxPurchase", attribute.MaxPurchase]}");
                return;
            }
        }

        // we need to force drop weapon first before make an purchase
        var weapons = client.PlayerPawn.Value?.WeaponServices?.MyWeapons;

        if (weapons == null)
        {
            _logger.LogError("[PurchaseWeapon] {0} Weapon service is somehow null", client.PlayerName);
            return;
        }

        var weapondrop = string.Empty;

        foreach (var weapon in weapons)
        {
            var slot = (int)weapon.Value!.GetVData<CCSWeaponBaseVData>()!.GearSlot;

            // for primary and secondary only.
            if (slot > 2)
                continue;

            if (slot == attribute.WeaponSlot)
            {
                // drop this weapon then break
                weapondrop = weapon.Value.DesignerName;
                break;
            }
        }

        Server.NextFrame(() =>
        {
            // we give weapon to them this part can't be null unless server manager fucked it up.
            if (attribute.WeaponEntity == "item_kevlar")
            {
                client.PlayerPawn.Value!.ArmorValue = 100;
                Utilities.SetStateChanged(client.PlayerPawn.Value, "CCSPlayerPawn", "m_ArmorValue");
            }

            else
            {
                if (!string.IsNullOrEmpty(weapondrop))
                {
                    // we drop the weapon first.
                    DropWeaponByDesignName(client, weapondrop);
                }

                client.GiveNamedItem(attribute.WeaponEntity!);
            }

            // update purchase history
            if (PlayerPurchaseCount![client].WeaponCount!.ContainsKey(attribute.WeaponEntity!))
                PlayerPurchaseCount![client].WeaponCount![attribute.WeaponEntity!]++;

            else
                PlayerPurchaseCount?[client].WeaponCount?.Add(attribute.WeaponEntity!, 1);

            var purchaseCount = PlayerPurchaseCount![client].WeaponCount![attribute.WeaponEntity!];

            var message = $" {Localizer["Prefix"]} {Localizer["Weapon.PurchaseSuccess", attribute.WeaponName!]}";

            if (attribute.MaxPurchase > 0)
                message += $" {Localizer["Weapon.PurchaseCount", attribute.MaxPurchase - purchaseCount, attribute.MaxPurchase]}";

            client.PrintToChat($"{message}");

            // updated their cash.
            client.InGameMoneyServices!.Account -= attribute.Price;
            Utilities.SetStateChanged(client, "CCSPlayerController", "m_pInGameMoneyServices");
        });
    }

    public static bool IsRestricted(string weaponentity)
    {
        if (WeaponsConfig == null)
            return false;

        var weapon = GetWeaponAttributeByEntityName(weaponentity);

        if (weapon == null)
            return false;

        return weapon.Restrict;
    }

    public static WeaponAttribute? GetWeaponAttributeByEntityName(string? weaponentity)
    {
        if (WeaponsConfig == null)
            return null;

        return WeaponsConfig.Where(data => data.Value.WeaponEntity == weaponentity).FirstOrDefault().Value;
    }

    public static WeaponAttribute? GetWeaponAttributeByName(string? weapon)
    {
        if (WeaponsConfig == null)
            return null;

        return WeaponsConfig.Where(data => string.Equals(data.Value.WeaponName, weapon, StringComparison.OrdinalIgnoreCase)).FirstOrDefault().Value;
    }

    public bool IsPlayerAlive(CCSPlayerController client)
    {
        if (client == null)
            return false;

        if (!client.IsValid)
        {
            _logger.LogError("[IsPlayerAlive] Client is invalid pointer!");
            return false;
        }

        var clientPawn = client.PlayerPawn.Value;

        if (clientPawn == null || !clientPawn.IsValid)
            return false;

        return (LifeState_t)clientPawn.LifeState == LifeState_t.LIFE_ALIVE;
    }

    public void DropWeaponByDesignName(CCSPlayerController client, string weaponName, bool remove = false)
    {
        if (client == null)
            return;

        var service = client.PlayerPawn.Value?.WeaponServices;
        var matchedWeapon = client.PlayerPawn.Value?.WeaponServices?.MyWeapons.Where(x => x.Value?.DesignerName == weaponName).FirstOrDefault();

        if (matchedWeapon != null && matchedWeapon.IsValid && service != null)
        {
            if (matchedWeapon.Value == null)
                return;

            DropWeapon(service.As<CCSPlayer_WeaponServices>(), matchedWeapon.Value);

            // set timer to remove if remove is true.
            if (remove)
            {
                AddTimer(1f, () =>
                {
                    matchedWeapon.Value?.AddEntityIOEvent("Kill", matchedWeapon.Value, null, "", 0.1f);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
    }
    
    public static void DropWeapon(CCSPlayer_WeaponServices services ,CBasePlayerWeapon weapon)
    {
        Guard.IsValidEntity(weapon);
        VirtualFunction.CreateVoid<nint, CBasePlayerWeapon, Vector?, Vector?>(services.Handle, GameData.GetOffset("CCSPlayer_WeaponServices_DropWeapon"))(services.Handle, weapon, null, null);
    }
}

public class WeaponAttribute
{
    public string? WeaponName { get; set; }
    public string? WeaponEntity { get; set; }
    public float Knockback { get; set; } = 1f;
    public int WeaponSlot { get; set; }
    public int Price { get; set; }
    public int MaxPurchase { get; set; } = 0;
    public bool Restrict { get; set; } = false;
    public List<string>? PurchaseCommand { get; set; } = [];
}

public class PurchaseCount
{
    public PurchaseCount()
    {
        WeaponCount = new Dictionary<string, int>();
    }

    public Dictionary<string, int>? WeaponCount { get; set; } = [];
}