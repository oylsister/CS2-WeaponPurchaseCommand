using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponPurchaseCommand
{
    public class WeaponPurchaseCommand : BasePlugin
    {
        public override string ModuleName => "CS2-Weapon Purchase Command";
        public override string ModuleVersion => "1.0";
        public override string ModuleAuthor => "Oylsister";
        public override string ModuleDescription => "Purchase weapon command for counter-strike 2";

        public ConfigFile? PurchaseConfig { get; set; }

        public bool ConfigIsLoaded { get; set; } = false;

        public Dictionary<CCSPlayerController, PurchaseHistory> PlayerBuyList { get; set; } = new Dictionary<CCSPlayerController, PurchaseHistory>();

        public override void Load(bool hotReload)
        {
            var configPath = Path.Combine(ModuleDirectory, "weapons.json");

            if(!File.Exists(configPath))
            {
                return;
            }

            PurchaseConfig = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(configPath));

            InitialCommand();
        }

        [GameEventHandler]
        public HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
        {
            PlayerBuyList.Add(@event.Userid, new PurchaseHistory());
            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var client = @event.Userid;
            PlayerBuyList[client].PlayerBuyHistory.Clear();
            PlayerBuyList.Remove(client);
            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var client = @event.Userid;
            PlayerBuyList[client].PlayerBuyHistory.Clear();
            return HookResult.Continue;
        }

        private void InitialCommand()
        {
            if (ConfigIsLoaded) 
            {
                return;
            }

            if (PurchaseConfig == null)
                return;

            foreach(var weapon in PurchaseConfig.WeaponConfigs) 
            {
                foreach(var command in weapon.Value.PurchaseCommand)
                {
                    AddCommand(command, "Buy Command", PurchaseWeaponCommand);
                }
            }

            ConfigIsLoaded = true;
        }

        public void PurchaseWeaponCommand(CCSPlayerController? client, CommandInfo info)
        {
            var weaponCommand = info.GetArg(0);

            foreach (string keyVar in PurchaseConfig!.WeaponConfigs.Keys) 
            {
                foreach (string command in PurchaseConfig.WeaponConfigs[keyVar].PurchaseCommand)
                {
                    if (String.Equals(weaponCommand, command))
                    {
                        PurchaseWeapon(client!, keyVar);
                    }
                }
            }
        }

        public void PurchaseWeapon(CCSPlayerController client, string weapon)
        {
            var weaponConfig = PurchaseConfig!.WeaponConfigs[weapon];

            if (weaponConfig == null)
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} Invalid weapon!");
                return;
            }

            if (!client.PawnIsAlive)
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} this feature need you to be alive!");
                return;
            }

            if (weaponConfig.PurchaseRestrict)
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} Weapon {ChatColors.Lime}{weapon}{ChatColors.Default} is restricted");
                return;
            }

            var cooldown = PurchaseConfig.PurchaseSettings!.CooldownPurchase;
            if (cooldown > 0 && PlayerBuyList[client].IsCooldownNow)
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} Your purchase is on cooldown now!");
                return;
            }

            var clientMoney = client.InGameMoneyServices!.Account;

            if (clientMoney < weaponConfig.PurchasePrice)
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} You don't have enough money to purchase this weapon!");
                return;
            }

            int weaponPurchased;
            bool weaponFound = PlayerBuyList[client].PlayerBuyHistory.TryGetValue(weapon, out weaponPurchased);

            if (weaponConfig.PurchaseLimit > 0)
            {
                if (weaponFound)
                {
                    if (weaponPurchased >= weaponConfig.PurchaseLimit)
                    {
                        client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} You have reached maximum purchase for {ChatColors.Lime}{weapon}{ChatColors.Default}, you can purchase again in next round");
                        return;
                    }
                    else
                    {
                        PlayerBuyList[client].PlayerBuyHistory[weapon] = weaponPurchased++;
                    }
                }
                else
                {
                    PlayerBuyList[client].PlayerBuyHistory.Add(weapon, 1);
                    weaponPurchased = 1;
                }

                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} You have purchase {ChatColors.Lime}{weapon}{ChatColors.Default}, Purchase Limit: {ChatColors.Green}{weaponConfig.PurchaseLimit - weaponPurchased}/{weaponConfig.PurchaseLimit}");
            }

            else
            {
                client.PrintToChat($"{ChatColors.Green}[Weapon]{ChatColors.Default} You have purchase {ChatColors.Lime}{weapon}{ChatColors.Default}.");
            }

            List<string> weaponEntitySlot = new List<string>();
            var weaponSlot = weaponConfig.WeaponSlot;

            foreach (string keyVar in PurchaseConfig!.WeaponConfigs.Keys)
            {
                var slots = PurchaseConfig.WeaponConfigs[keyVar].WeaponSlot;

                if(weaponSlot == slots)
                {
                    weaponEntitySlot.Add(PurchaseConfig.WeaponConfigs[keyVar].WeaponEntity!);
                }
            }

            foreach (var clientWeapon in client.PlayerPawn.Value.WeaponServices!.MyWeapons)
            {
                bool found = false;

                if (found)
                    break;

                foreach (var weaponEntitnyName in weaponEntitySlot)
                {
                    if (String.Equals(clientWeapon.Value.DesignerName, weaponEntitnyName))
                    {
                        clientWeapon.Value.Remove();
                        found = true;
                        break;
                    }
                }
            }

            client.GiveNamedItem(weaponConfig.WeaponEntity!);
            client.InGameMoneyServices!.Account = clientMoney - weaponConfig.PurchasePrice;
            PlayerBuyList[client].IsCooldownNow = true;

            AddTimer(cooldown, () =>
            {
                PlayerBuyList[client].IsCooldownNow = false;
            });
        }
    }
}

public class ConfigFile
{
    [JsonPropertyName("Settings")]
    public PurchaseSetting? PurchaseSettings { get; set; }

    [JsonPropertyName("Weapons")]
    public Dictionary<string, WeaponConfig> WeaponConfigs { get; set; } = new Dictionary<string, WeaponConfig>();
}

public class PurchaseSetting
{
    [JsonPropertyName("cooldown")]
    public float CooldownPurchase { get; set; } = 0f;
}

public class PurchaseHistory
{
    public Dictionary<string, int> PlayerBuyHistory { get; set; } = new Dictionary<string, int>();
    public bool IsCooldownNow { get; set; } = false;
}

public class WeaponConfig
{
    public WeaponConfig(List<string> purchaseCommand, string weaponEntity, int slot, int price, int limitbuy, bool restrict)
    {
        PurchaseCommand = purchaseCommand;
        WeaponEntity = weaponEntity;
        WeaponSlot = slot;
        PurchasePrice = price;
        PurchaseLimit = limitbuy;
        PurchaseRestrict = restrict;
    }

    [JsonPropertyName("command")]
    public List<string> PurchaseCommand { get; set; } = new List<string>();

    [JsonPropertyName("weaponentity")]
    public string? WeaponEntity { get; set; }

    [JsonPropertyName("weaponslot")]
    public int WeaponSlot { get; set; }

    [JsonPropertyName("price")]
    public int PurchasePrice { get; set; }

    [JsonPropertyName("maxpurchase")]
    public int PurchaseLimit { get; set; } = 0;

    [JsonPropertyName("restrict")]
    public bool PurchaseRestrict { get; set; }
}