using System.Collections.Generic;
using ModKit;
using NeoRune;
using UE.Angelscript;
using UE.CommonGame;
using UE.CommonInput;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Engine;
using UE.GameplayTags;
using UE.InputCore;
using UE.InventorySystem;
using UE.OreUI;
using UE.SlateCore;
using UE.SpicewoodUI;
using UE.SWCoreCommon;
using UE.SWCoreGameplay;
using UE.SWPlayerState;
using UE.UMG;

namespace Loadouts;

/// <summary>
/// Loadouts: save the gear the character has on as a loadout, and switch between loadouts in one press. With enchantments
/// moved too: the gear taken off is disenchanted (the game gives its enchantment points back) and the gear put on is
/// enchanted again as it was saved. Loadouts are saved per character. They live on the inventory page: a Loadouts button
/// beside Effects &amp; Stats opens a drawer over the page's item panel. Built on the mod kit (ModKit).
/// </summary>
[ModSetting.Heading("Loadouts")]
[ModSetting.Keybind("menuKey", "Open the loadouts on the inventory page (its Loadouts button does too)", Default = "L", Secondary = "Gamepad_RightThumbstick")]
[ModSetting.Keybind("nextKey", "Switch to the next saved loadout (anywhere)")]
[ModSetting.Slider("count", "Number of loadouts", Default = 4, Min = 2, Max = 6, Step = 1)]
[ModSetting.Toggle("moveEnchants", "Move enchantments with the gear (disenchants the gear you take off, enchants the gear you put on as it was saved)", Default = true)]
[ModSetting.Toggle("updates", "Tell me when a new version is out (checks GitHub when the game starts)", Default = true)]
[ModSetting.Heading("Troubleshooting")]
// Diagnostics: on in development builds (dotnet build), off in releases (Release builds).
#if DEBUG
[ModSetting.Toggle("debug", "Write diagnostics to the mod's log", Default = true)]
#else
[ModSetting.Toggle("debug", "Write diagnostics to the mod's log", Default = false)]
#endif
public class ModActor : AActor, IModSettings
{
    // What a step of a switch does to its item.
    const int StepDisenchant = 0;
    const int StepEquip = 1;
    const int StepEnchant = 2;
    const double StepSeconds = 0.15;   // between steps, so the game has applied one before the next looks
    const int StepTriesMax = 12;
    const double ButtonGap = 10;       // UI pixels between the Loadouts button and Effects & Stats

    // Settings, with the same defaults as above (Blueprint Loader only sends the ones the player changed).
    public int Count = 4;
    public bool MoveEnchants = true;
    public bool CheckUpdates = true;
#if DEBUG
    public bool Debug = true;
#else
    public bool Debug = false;
#endif
    FKey menuKey = new FKey { KeyName = "L" };
    FKey menuPadKey = new FKey { KeyName = "Gamepad_RightThumbstick" };
    FKey nextKey = new FKey { KeyName = "None" };
    FKey nextPadKey = new FKey { KeyName = "None" };

    bool started;
    int startTries;
    // The character the loadouts belong to, and theirs.
    string character = "";
    SavedLoadouts saved;
    // The last thing done, for the drawer (and a toast when it isn't open).
    public string Status = "";
    // Goes up whenever the loadouts change, so the drawer redraws what it shows.
    public int Revision;

    // Keys: "next loadout" in the world, with the usual listeners. On the inventory page (a menu, which they don't hear),
    // the game's key tracker, switched on only while the page shows, as it keeps the keys it tracks from the game's menus.
    KeyListener? nextListener;
    KeyListener? nextPadListener;
    MenuKeys? menuKeys;
    // While the drawer is open, its keys (they're kept from the page under it), and the typing keys while a name's typed.
    MenuKeys? drawerKeys;
    MenuKeys? typeKeys;
    // And the left stick, for moving through the loadouts (its directions don't come as keys).
    MenuStick? drawerStick;
    int stickLogs;
    // Keeps the inventory's focus where it is while the drawer's open (the stick would move it under the drawer).
    NavigationLock? pageLock;
    string pageLockLogged = "";
    double lastAction = -100;

    // The inventory page: the inventory screens the game has made, the page shown, its Effects & Stats button, and the
    // Loadouts button (a copy of it, on screen just above it). Then the drawer, and where it goes.
    WidgetWatcher? inventoryWatcher;
    List<UCommonActivatableWidget> inventories = new();
    UAS_PlayerInventoryBase? page;
    UCommonButtonBase? statsButton;
    StyledButton? loadoutsButton;
    bool loggedPage;
    bool loggedNoStatsButton;
    LoadoutDrawer? drawer;
    bool drawerRectKnown;
    FVector2D drawerTopLeft;
    FVector2D drawerBottomRight;
    // The game's button icons, by key.
    ActionIcons? icons;
    double lastIconSearch = -100;

    // The switch in progress (to loadout `switching`), as steps done one at a time.
    int switching = -1;
    // Why the enchantment being applied wasn't (for the log), and whether it was for want of points or Spring Stones.
    string enchantWhy = "";
    bool enchantWasShort;
    List<int> stepKinds = new();
    List<SavedItem> stepItems = new();
    int stepAt;
    int stepTries;
    double nextStepAt;
    int equippedCount;
    int missingCount;
    string missingNames = "";
    int failedCount;
    int disenchanted;
    int enchanted;
    int enchantShort;
    int enchantFailed;
    int stonesBefore;
    int pointsBefore;
    int lastLoadout = -1;

    protected override void ReceiveBeginPlay()
    {
        // Also while the game is paused, so switching works with a menu open if the game pauses for it.
        SetTickableWhenPaused(true);
        Timer.Start(this, nameof(TryStart), 0.5f, true);
        // A moment in, once the settings have come (also on the main menu).
        Timer.Start(this, nameof(StartUpdateCheck), 2f, false);
    }

    void StartUpdateCheck()
    {
        if (CheckUpdates) UpdateCheck.Start(this);
    }

    /// <summary>Waits for the player, their inventory and their character's id (there's none on the main menu).</summary>
    void TryStart()
    {
        if (started) return;
        var player = World.Player(this);
        if (player == null || World.PlayerController(this) == null) return;
        var inventory = Inventory();
        if (inventory == null) return;
        // The character's id comes with the player state: wait a few seconds for it.
        startTries++;
        if (CharacterId() == "default" && startTries < 10) return;
        started = true;
        Timer.Stop(this, nameof(TryStart));
        character = CharacterId();
        LoadLoadouts();
        // The loadout on now counts as the last one used: upgrades made to its enchantments are kept when swapping away.
        for (int i = 0; i < Count && lastLoadout < 0; i++)
            if (IsWorn(i)) lastLoadout = i;
        ListenToKeys();
        inventoryWatcher = WidgetWatcher.Start(this, Unreal.ClassOf<UAS_PlayerInventory_Activatable>(), 0.25f);
        if (inventoryWatcher != null) inventoryWatcher.Found += OnInventory;
        if (Debug)
        {
            // Each line written loads and saves the whole log: start afresh once it's long.
            if (Log.ReadLines().Count > 1500) Log.Clear();
            LogStart(player, inventory);
        }
    }

    protected override void ReceiveEndPlay(EEndPlayReason EndPlayReason)
    {
        CloseDrawer();
        menuKeys?.SetOn(false);
        drawerKeys?.SetOn(false);
        typeKeys?.SetOn(false);
        drawerStick?.SetOn(false);
        pageLock?.Release();
        loadoutsButton?.Hide();
    }

    public override void ReceiveTick(float deltaSeconds)
    {
        if (!started) return;
        double now = World.RealTime(this);
        bool drawerOpen = DrawerOpen();
        bool buttonShown = UpdatePage(now, drawerOpen);
        // The drawer goes with the page: when the inventory closes or changes tab, or its effects panel opens.
        if (drawerOpen && !PageShown())
        {
            CloseDrawer();
            drawerOpen = false;
        }
        menuKeys?.SetOn(buttonShown && !drawerOpen);
        bool renaming = drawerOpen && drawer != null && drawer.IsRenaming();
        // Each stays on until its keys are let go after it's done with: the page doesn't get the end of a press it didn't
        // see the start of (B closing the drawer mustn't close the inventory as it's let go).
        if (drawerKeys != null) drawerKeys.SetOn((drawerOpen && !renaming) || (drawerKeys.IsOn && drawerKeys.AnyDown()));
        if (typeKeys != null) typeKeys.SetOn(renaming || (typeKeys.IsOn && typeKeys.AnyDown()));
        drawerStick?.SetOn(drawerOpen && !renaming);
        HoldPage(drawerOpen);
        if (drawerOpen && drawer != null) drawer.Refresh(now);
        if (switching >= 0 && now >= nextStepAt)
        {
            nextStepAt = now + StepSeconds;
            RunStep();
        }
    }

    // ---------------------------------------------------------------- Settings

    public void OnSettingChanged(string id, string value)
    {
        switch (id)
        {
            case "count": Count = Clamp((int)(ModSettings.ToNumber(value) + 0.5), 2, LoadoutDrawer.Max); break;
            case "moveEnchants": MoveEnchants = ModSettings.ToBool(value); break;
            case "updates": CheckUpdates = ModSettings.ToBool(value); break;
            case "debug": Debug = ModSettings.ToBool(value); break;
        }
    }

    public void OnKeybindChanged(string id, FKey key, FKey secondaryKey)
    {
        if (id == "menuKey")
        {
            menuKey = key;
            menuPadKey = secondaryKey;
        }
        else if (id == "nextKey")
        {
            nextKey = key;
            nextPadKey = secondaryKey;
        }
        else return;
        if (started) ListenToKeys();
    }

    public void OnSettingsReset()
    {
        Count = 4;
        MoveEnchants = true;
        CheckUpdates = true;
#if DEBUG
        Debug = true;
#else
        Debug = false;
#endif
        menuKey = new FKey { KeyName = "L" };
        menuPadKey = new FKey { KeyName = "Gamepad_RightThumbstick" };
        nextKey = new FKey { KeyName = "None" };
        nextPadKey = new FKey { KeyName = "None" };
        if (started) ListenToKeys();
    }

    static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    // ---------------------------------------------------------------- Keys

    void ListenToKeys()
    {
        nextListener?.Stop();
        nextPadListener?.Stop();
        nextListener = Listen(nextKey);
        if (nextListener != null) nextListener.Pressed += OnNextKey;
        nextPadListener = Listen(nextPadKey);
        if (nextPadListener != null) nextPadListener.Pressed += OnNextKey;

        if (menuKeys == null)
        {
            menuKeys = MenuKeys.Create(this);
            if (menuKeys != null) menuKeys.Pressed += OnMenuKey;
            else if (Debug) Log.Write("keys: couldn't set up the game's key tracker, keys won't work in the inventory");
        }
        if (menuKeys != null)
        {
            menuKeys.Clear();
            menuKeys.Track(menuKey);
            menuKeys.Track(menuPadKey);
        }

        if (drawerKeys == null)
        {
            drawerKeys = MenuKeys.Create(this);
            if (drawerKeys != null) drawerKeys.Pressed += OnDrawerKey;
        }
        if (drawerKeys != null)
        {
            drawerKeys.Clear();
            TrackDrawerKeys(drawerKeys);
        }
        if (drawerStick == null)
        {
            drawerStick = MenuStick.Create(this);
            if (drawerStick != null) drawerStick.Stepped += OnDrawerStick;
            else if (Debug) Log.Write("keys: couldn't set up the game's stick reader, the left stick won't move through the loadouts");
        }
        if (typeKeys == null)
        {
            typeKeys = MenuKeys.Create(this);
            if (typeKeys != null)
            {
                // Held keys type again and again (Backspace most of all), like the game's text boxes.
                typeKeys.Repeats = true;
                if (Debug) typeKeys.LogReports = 40;
                typeKeys.Pressed += OnTypeKey;
                TrackTypingKeys(typeKeys);
            }
        }
    }

    /// <summary>The drawer's keys: moving, its actions, and closing (B, Esc, the pad's menu button, the loadouts key).</summary>
    void TrackDrawerKeys(MenuKeys keys)
    {
        keys.Track(Key("Up"));
        keys.Track(Key("Down"));
        keys.Track(Key("Gamepad_DPad_Up"));
        keys.Track(Key("Gamepad_DPad_Down"));
        keys.Track(Key("Enter"));
        keys.Track(Key("Gamepad_FaceButton_Bottom"));
        keys.Track(Key("S"));
        keys.Track(Key("Gamepad_FaceButton_Left"));
        keys.Track(Key("R"));
        keys.Track(Key("Gamepad_FaceButton_Top"));
        keys.Track(Key("Delete"));
        keys.Track(Key("Gamepad_Special_Left"));
        keys.Track(Key("Escape"));
        keys.Track(Key("Gamepad_FaceButton_Right"));
        keys.Track(Key("Gamepad_Special_Right"));
        keys.Track(menuKey);
        keys.Track(menuPadKey);
    }

    /// <summary>The keys for typing a loadout's name: letters, digits, space, dash, Shift, Backspace, Enter and Esc (and A and B).</summary>
    void TrackTypingKeys(MenuKeys keys)
    {
        keys.Track(Key("A")); keys.Track(Key("B")); keys.Track(Key("C")); keys.Track(Key("D")); keys.Track(Key("E"));
        keys.Track(Key("F")); keys.Track(Key("G")); keys.Track(Key("H")); keys.Track(Key("I")); keys.Track(Key("J"));
        keys.Track(Key("K")); keys.Track(Key("L")); keys.Track(Key("M")); keys.Track(Key("N")); keys.Track(Key("O"));
        keys.Track(Key("P")); keys.Track(Key("Q")); keys.Track(Key("R")); keys.Track(Key("S")); keys.Track(Key("T"));
        keys.Track(Key("U")); keys.Track(Key("V")); keys.Track(Key("W")); keys.Track(Key("X")); keys.Track(Key("Y"));
        keys.Track(Key("Z"));
        keys.Track(Key("Zero")); keys.Track(Key("One")); keys.Track(Key("Two")); keys.Track(Key("Three")); keys.Track(Key("Four"));
        keys.Track(Key("Five")); keys.Track(Key("Six")); keys.Track(Key("Seven")); keys.Track(Key("Eight")); keys.Track(Key("Nine"));
        keys.Track(Key("SpaceBar"));
        keys.Track(Key("Hyphen"));
        keys.Track(Key("BackSpace"));
        keys.Track(Key("Enter"));
        keys.Track(Key("Escape"));
        keys.Track(Key("LeftShift"));
        keys.Track(Key("RightShift"));
        keys.Track(Key("Gamepad_FaceButton_Bottom"));
        keys.Track(Key("Gamepad_FaceButton_Right"));
    }

    static FKey Key(string name) => new FKey { KeyName = name };

    /// <summary>A key for the drawer (only while it's open: the tracker stays on a moment after, until its keys are let go).</summary>
    void OnDrawerKey()
    {
        if (drawerKeys == null || drawer == null || !DrawerOpen() || drawer.IsRenaming()) return;
        drawer.Key(drawerKeys.LastKey, false);
    }

    /// <summary>While the drawer's open, the inventory's focused item stays put (and is let go when it closes).</summary>
    void HoldPage(bool drawerOpen)
    {
        if (pageLock == null) pageLock = NavigationLock.Create(this);
        if (pageLock == null) return;
        if (drawerOpen) pageLock.Hold();
        else if (pageLock.Holding()) pageLock.Release();
        if (!Debug) return;
        string held = pageLock.Describe();
        if (held == pageLockLogged) return;
        pageLockLogged = held;
        Log.Write("drawer holds the page's focus on: " + held);
    }

    /// <summary>The left stick pushed (or held) while the drawer's open: up and down move through the loadouts.</summary>
    void OnDrawerStick()
    {
        if (drawerStick == null || drawer == null || !DrawerOpen() || drawer.IsRenaming()) return;
        if (Debug && stickLogs < 4)
        {
            stickLogs++;
            Log.Write($"stick: step {drawerStick.Step} at ({drawerStick.Last.X:0.00}, {drawerStick.Last.Y:0.00})");
        }
        drawer.Stick(drawerStick.Step);
    }

    /// <summary>A typing key, while a loadout's name is typed.</summary>
    void OnTypeKey()
    {
        if (typeKeys == null || drawer == null || !DrawerOpen() || !drawer.IsRenaming()) return;
        bool shift = typeKeys.IsDown(Key("LeftShift")) || typeKeys.IsDown(Key("RightShift"));
        drawer.Key(typeKeys.LastKey, shift);
    }

    /// <summary>A listener for a key in the world; none for an unbound key ("None": NeoRune 1.0 can't write empty names).</summary>
    KeyListener? Listen(FKey key)
    {
        if (key.KeyName == "None") return null;
        return Input.OnKey(this, key);
    }

    /// <summary>Whether a key is the one that opens (and closes) the loadouts.</summary>
    public bool IsMenuKey(FKey key) => MenuKeys.Is(key, menuKey) || MenuKeys.Is(key, menuPadKey);

    /// <summary>A key on the inventory page (the tracker only hears the loadouts' keys).</summary>
    void OnMenuKey()
    {
        if (menuKeys != null && IsMenuKey(menuKeys.LastKey)) OpenDrawer();
    }

    /// <summary>True (and starts the moment) unless an action just happened: one press can arrive through two routes.</summary>
    bool ActionAllowed()
    {
        double now = World.RealTime(this);
        if (now - lastAction < 0.3) return false;
        lastAction = now;
        return true;
    }

    void OnNextKey()
    {
        if (!ActionAllowed()) return;
        NextLoadout();
    }

    public bool Gamepad() => Screens.Gamepad(this);

    /// <summary>Whether the player is on the mouse and keyboard (not a controller).</summary>
    public bool UsingMouse() => !Screens.Gamepad(this);

    /// <summary>The game's input action row on a key (on the pad or the keyboard), for its icon.</summary>
    public bool IconRow(string key, bool gamepad, out FDataTableRowHandle row)
    {
        if (icons != null) return icons.Row(key, gamepad, out row);
        row = new FDataTableRowHandle();
        return false;
    }

    /// <summary>Looks for the game's button icons (their tables come with its menus: a few tries).</summary>
    void FindIconRows()
    {
        if (icons == null) icons = ActionIcons.Create(this);
        if (icons == null) return;
        icons.Find();
        if (Debug) Log.Write($"icons: keyboard keys with icons:{icons.KeyboardKeys()}");
    }

    /// <summary>A key's short name, when there's no icon for it: "R3" for the right stick's click, "L"...</summary>
    public static string KeyLabel(FKey key) => ActionIcons.Label(key);

    // ---------------------------------------------------------------- The inventory page

    void OnInventory(UUserWidget widget)
    {
        var screen = widget as UCommonActivatableWidget;
        if (screen != null) inventories.Add(screen);
        if (Debug) Log.Write($"inventory screen found: {GameUI.ClassPath(widget)}");
    }

    /// <summary>The inventory page on screen (the full inventory's, not the mini inventory), or null.</summary>
    UAS_PlayerInventoryBase? OpenPage()
    {
        for (int i = inventories.Count - 1; i >= 0; i--)
        {
            var screen = inventories[i];
            if (!UKismetSystemLibrary.IsValid(screen))
            {
                inventories.RemoveAt(i);
                continue;
            }
            // The full inventory (the mini inventory is one too, but it's not the page).
            if (screen as UAS_MiniInventory_Activatable != null) continue;
            var full = screen as UAS_PlayerInventory_Activatable;
            var inventoryPage = full != null ? full.PlayerInventory : null;
            if (Debug) LogPageState(screen, inventoryPage);
            // Its showing is what counts: found in game, it flips between activated and not while it shows. All the way up:
            // the menu's tabs (map, collectibles...) are a switcher above the page, which hides it without changing it.
            if (inventoryPage != null && screen.IsVisible() && inventoryPage.IsVisible() && Screens.Shown(inventoryPage, null)) return inventoryPage;
        }
        return null;
    }

    string pageState = "";
    int pageStates;

    /// <summary>The inventory page's state when it changes (the first few times), to see what tells it's on screen.</summary>
    void LogPageState(UCommonActivatableWidget screen, UAS_PlayerInventoryBase? inventoryPage)
    {
        if (pageStates >= 6) return;
        string state = $"{UKismetSystemLibrary.GetObjectName(screen)}: visible {screen.IsVisible()}, page {GameUI.ClassPath(inventoryPage)}";
        if (inventoryPage != null) state += $", page visible {inventoryPage.IsVisible()}, shown: {Screens.ShownTrace(inventoryPage, null)}";
        if (state == pageState) return;
        pageState = state;
        pageStates++;
        Log.Write("inventory screen " + state);
    }

    /// <summary>The page's "switch" action (S: Effects &amp; Stats), whichever version of the page it is.</summary>
    static FDataTableRowHandle SwitchAction(UAS_PlayerInventoryBase inventoryPage)
    {
        var classic = inventoryPage as UAS_PlayerInventory;
        if (classic != null) return classic.SwitchInputData;
        var revamp = inventoryPage as UAS_PlayerInventoryRevamp;
        if (revamp != null) return revamp.SwitchInputData;
        return new FDataTableRowHandle();
    }

    /// <summary>
    /// Finds the page's Effects &amp; Stats button and shows the Loadouts button beside it while it's on screen. True while
    /// the Loadouts button shows.
    /// </summary>
    bool UpdatePage(double now, bool drawerOpen)
    {
        var current = OpenPage();
        if (current != page)
        {
            page = current;
            statsButton = null;
        }
        if (page != null && (statsButton == null || !UKismetSystemLibrary.IsValid(statsButton))) statsButton = FindStatsButton(page);
        bool show = page != null && statsButton != null && Screens.ShownTrace(statsButton, page) == "yes" && !drawerOpen && !EffectsPanelShown();
        bool iconsWanted = icons == null || (icons.Searches < 3 && !icons.Has($"{menuPadKey.KeyName}", true));
        if (show && iconsWanted && now - lastIconSearch > 1)
        {
            lastIconSearch = now;
            FindIconRows();
        }
        return PlaceButton(show, now);
    }

    /// <summary>
    /// The page's Effects &amp; Stats button: the one its "switch" action (S) triggers, else the one saying "Effects". Of
    /// the buttons showing on the page.
    /// </summary>
    UCommonButtonBase? FindStatsButton(UAS_PlayerInventoryBase inventoryPage)
    {
        string switchRow = $"{SwitchAction(inventoryPage).RowName}";
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var all, Unreal.ClassOf<UCommonButtonBase>(), false);
        UCommonButtonBase? found = null;
        UCommonButtonBase? byText = null;
        int candidates = 0;
        foreach (var widget in all)
        {
            var button = widget as UCommonButtonBase;
            if (button == null || Screens.ShownTrace(button, inventoryPage) != "yes") continue;
            candidates++;
            if (switchRow != "None" && $"{button.TriggeringInputAction.RowName}" == switchRow)
            {
                found = button;
                break;
            }
            if (byText == null && UKismetStringLibrary.Contains(ButtonLabel(button), "Effects", false, false)) byText = button;
        }
        if (found == null) found = byText;
        if (found == null)
        {
            if (Debug && !loggedNoStatsButton) Log.Write($"inventory page: no Effects & Stats button among {candidates} buttons showing (its switch action is {switchRow})");
            loggedNoStatsButton = true;
            return null;
        }
        if (Debug && !loggedPage) LogPage(inventoryPage, found, switchRow);
        loggedPage = true;
        return found;
    }

    static string ButtonLabel(UCommonButtonBase button)
    {
        var ore = button as UOreUIButtonBase;
        if (ore == null) return "";
        string label = UKismetTextLibrary.Conv_TextToString(ore.ButtonText);
        if (UKismetStringLibrary.Len(label) == 0 && ore.TextBlockWidget != null) label = UKismetTextLibrary.Conv_TextToString(ore.TextBlockWidget.GetText());
        return label;
    }

    /// <summary>
    /// The Loadouts button (the kit's <see cref="StyledButton"/>, made like Effects &amp; Stats), on screen right of Effects
    /// &amp; Stats, as high, across to the right edge of the item panel below. True while it shows.
    /// </summary>
    bool PlaceButton(bool show, double now)
    {
        if (!show || statsButton == null)
        {
            loadoutsButton?.Hide();
            return false;
        }
        if (loadoutsButton == null || !UKismetSystemLibrary.IsValid(loadoutsButton))
        {
            loadoutsButton = Ui.Create(this, Unreal.ClassOf<StyledButton>()) as StyledButton;
            if (loadoutsButton != null)
            {
                loadoutsButton.SetLabel("Loadouts");
                loadoutsButton.Clicked += OnLoadoutsButton;
            }
        }
        if (loadoutsButton == null) return false;
        if (!StatsRect(out FVector2D statsTopLeft, out FVector2D statsBottomRight)) return true;
        double scale = PageScale();
        if (!(scale > 0)) return true;
        double gap = ButtonGap * scale;
        double height = statsBottomRight.Y - statsTopLeft.Y;
        var topLeft = Vec2(statsBottomRight.X + gap, statsTopLeft.Y);
        var bottomRight = Vec2(ColumnRight(now, statsTopLeft, statsBottomRight), statsBottomRight.Y);
        if (!(bottomRight.X - topLeft.X > height * 1.5))
        {
            // No room at its right: above it instead, as wide.
            topLeft = Vec2(statsTopLeft.X, statsTopLeft.Y - height - gap);
            bottomRight = Vec2(statsBottomRight.X, statsTopLeft.Y - gap);
        }
        bool gamepad = Gamepad();
        var key = gamepad && menuPadKey.KeyName != "None" ? menuPadKey : menuKey;
        bool hasRow = IconRow($"{key.KeyName}", gamepad, out FDataTableRowHandle row);
        loadoutsButton.Show(statsButton, topLeft, bottomRight, row, hasRow);
        if (Debug && !loggedButtonPlace)
        {
            loggedButtonPlace = true;
            Log.Write($"loadouts button at ({topLeft.X:0}, {topLeft.Y:0}) to ({bottomRight.X:0}, {bottomRight.Y:0}), page scale {scale:0.000}");
        }
        return true;
    }

    bool loggedButtonPlace;
    double itemPanelAt = -100;
    bool itemPanelKnown;
    FVector2D itemPanelTopLeft;
    FVector2D itemPanelBottomRight;

    /// <summary>The Effects &amp; Stats button's rectangle, in absolute coordinates.</summary>
    bool StatsRect(out FVector2D topLeft, out FVector2D bottomRight)
    {
        if (statsButton != null) return Screens.Rect(statsButton, out topLeft, out bottomRight);
        topLeft = Vec2(0, 0);
        bottomRight = Vec2(0, 0);
        return false;
    }

    /// <summary>
    /// The inventory page's scale: absolute pixels per unit of its design (it's laid out at 4K sizes and scaled to fit),
    /// measured on Effects &amp; Stats.
    /// </summary>
    public double PageScale() => statsButton != null ? Screens.Scale(statsButton) : 0;

    /// <summary>
    /// The right edge of the page's right-hand column: the item panel under Effects &amp; Stats (looked for twice a second),
    /// else as far right of the button as it usually is.
    /// </summary>
    double ColumnRight(double now, FVector2D statsTopLeft, FVector2D statsBottomRight)
    {
        if (now - itemPanelAt > 0.5)
        {
            itemPanelAt = now;
            itemPanelKnown = FindItemPanel(statsTopLeft, statsBottomRight, out itemPanelTopLeft, out itemPanelBottomRight);
        }
        double fallback = statsBottomRight.X + (statsBottomRight.X - statsTopLeft.X) * 0.45;
        return itemPanelKnown && itemPanelBottomRight.X > statsBottomRight.X ? itemPanelBottomRight.X : fallback;
    }

    /// <summary>The item panel under Effects &amp; Stats: the largest item tooltip showing below it, overlapping it sideways.</summary>
    bool FindItemPanel(FVector2D statsTopLeft, FVector2D statsBottomRight, out FVector2D topLeft, out FVector2D bottomRight)
    {
        topLeft = Vec2(0, 0);
        bottomRight = Vec2(0, 0);
        UWidgetBlueprintLibrary.GetAllWidgetsOfClass(this, out var tips, Unreal.ClassOf<UAS_SpicewoodHoveredItemTooltip>(), false);
        double best = 0;
        foreach (var tip in tips)
        {
            if (tip == null || page == null || Screens.ShownTrace(tip, page) != "yes") continue;
            var geometry = tip.GetCachedGeometry();
            var size = USlateBlueprintLibrary.GetLocalSize(geometry);
            if (!(size.X > 1) || !(size.Y > 1)) continue;
            var a = USlateBlueprintLibrary.LocalToAbsolute(geometry, Vec2(0, 0));
            var b = USlateBlueprintLibrary.LocalToAbsolute(geometry, size);
            if (b.X < statsTopLeft.X || a.X > statsBottomRight.X || a.Y < statsTopLeft.Y) continue;
            double area = (b.X - a.X) * (b.Y - a.Y);
            if (area <= best) continue;
            best = area;
            topLeft = a;
            bottomRight = b;
        }
        return best > 0;
    }

    /// <summary>Whether the page's Effects &amp; Enchantments panel is open (it covers where the Loadouts button goes).</summary>
    bool EffectsPanelShown()
    {
        var classic = page as UAS_PlayerInventory;
        var revamp = page as UAS_PlayerInventoryRevamp;
        UWidget? effects = classic != null ? classic.TotalEffectsPanel : revamp != null ? revamp.TotalEffectsPanel : null;
        return effects != null && page != null && Screens.ShownTrace(effects, page) == "yes";
    }

    void OnLoadoutsButton() => OpenDrawer();

    bool DrawerOpen() => drawer != null && UKismetSystemLibrary.IsValid(drawer) && drawer.IsInViewport();

    /// <summary>Whether the inventory page the drawer goes over is still on screen (with Effects &amp; Stats, not its panel).</summary>
    bool PageShown() => page != null && statsButton != null && UKismetSystemLibrary.IsValid(statsButton)
        && Screens.ShownTrace(statsButton, page) == "yes" && !EffectsPanelShown();

    /// <summary>
    /// Opens the loadouts drawer over the inventory page (only from that page). It's on the screen over the page (above
    /// the Loadouts button), not one of the game's screens: see <see cref="LoadoutDrawer"/>.
    /// </summary>
    void OpenDrawer()
    {
        if (!started || DrawerOpen() || !PageShown() || !ActionAllowed()) return;
        CheckCharacter();
        MeasureDrawer();
        CollectStyle();
        if (Debug && !dumpedStyles) DumpStyles();
        Trail.Begin("open the loadouts drawer");
        if (drawer == null || !UKismetSystemLibrary.IsValid(drawer)) drawer = Ui.Create(this, Unreal.ClassOf<LoadoutDrawer>()) as LoadoutDrawer;
        if (drawer != null)
        {
            drawer.Begin(this, StartingLoadout());
            drawer.AddToViewport(10001);
        }
        Trail.End("open the loadouts drawer");
        loadoutsButton?.Hide();
        if (Debug) Log.Write($"loadouts drawer {(drawer != null ? "opened" : "didn't open")} at ({drawerTopLeft.X:0}, {drawerTopLeft.Y:0}) to ({drawerBottomRight.X:0}, {drawerBottomRight.Y:0}){(drawerRectKnown ? "" : " (not measured)")}, page scale {PageScale():0.000}; style: font {(fontObject != null ? "yes" : "no")}, entry box {(entryKnown ? "yes" : "no")}, {rarityNames.Count} rarity frames");
    }

    /// <summary>Takes the drawer off the screen (the Loadouts button comes back with the page).</summary>
    public void CloseDrawer()
    {
        if (!DrawerOpen() || drawer == null) return;
        drawer.RemoveFromParent();
        Sounds.Hover(this);
        if (Debug) Log.Write("loadouts drawer closed");
    }

    // ---------------------------------------------------------------- The game's look

    // Taken from the inventory page when the drawer opens: the game's font (FF_Spicewood, from Effects & Stats' label),
    // the item panel's entry box (its enchantment box), and the gear slots' rarity frames.
    UObject? fontObject;
    FSlateBrush entryBrush;
    bool entryKnown;
    List<string> rarityNames = new();
    List<FSlateBrush> rarityBrushes = new();
    // The slots' background material (MI_RaritySlot_BG): its rarity colour is a parameter, so the drawer can have frames
    // for rarities nobody's wearing (made from it). And the slots' "enchanted" mark.
    UMaterialInterface? rarityMaterial;
    FSlateBrush rarityTemplate;
    List<string> madeRarities = new();
    List<FSlateBrush> madeBrushes = new();
    FSlateBrush enchantMark;
    bool enchantMarkKnown;

    void CollectStyle()
    {
        // Afresh each time: the page makes new materials when it rebuilds its slots.
        rarityNames.Clear();
        rarityBrushes.Clear();
        var ore = statsButton as UOreUIButtonBase;
        if (ore != null && ore.TextBlockWidget != null && ore.TextBlockWidget.Font.FontObject != null) fontObject = ore.TextBlockWidget.Font.FontObject;
        if (page == null) return;
        var classic = page as UAS_PlayerInventory;
        var revamp = page as UAS_PlayerInventoryRevamp;
        UUserWidget? tooltip = classic != null ? classic.CompactTooltip : revamp != null ? revamp.CompactTooltip : null;
        var entry = tooltip != null ? GameUI.Find(tooltip, "NotBG") as UImage : null;
        if (entry == null && tooltip != null) entry = GameUI.Find(tooltip, "EnchantedBG") as UImage;
        if (entry != null && entry.Brush.ResourceObject != null)
        {
            entryBrush = entry.Brush;
            entryKnown = true;
        }
        // The gear slots' frames, by the rarity of what's in them.
        var inventory = Inventory();
        if (inventory == null || page.GearOverviewSlots == null) return;
        foreach (var worn in Worn(inventory))
        {
            string rarity = Items.TagName(worn.ItemData.RarityTag);
            if (rarityNames.Contains(rarity)) continue;
            var slotWidget = GameUI.Find(page.GearOverviewSlots, SlotWidgetName(Items.TagName(worn.EquippedSlot))) as UUserWidget;
            var frame = slotWidget != null ? GameUI.Find(slotWidget, "RarityBG") as UImage : null;
            if (frame == null || frame.Brush.ResourceObject == null) continue;
            rarityNames.Add(rarity);
            rarityBrushes.Add(frame.Brush);
            var instance = frame.Brush.ResourceObject as UMaterialInstance;
            if (rarityMaterial == null && instance != null && instance.Parent != null)
            {
                rarityMaterial = instance.Parent;
                rarityTemplate = frame.Brush;
            }
            var mark = slotWidget != null ? GameUI.Find(slotWidget, "EnchantedIndicator") as UImage : null;
            if (!enchantMarkKnown && mark != null && mark.Brush.ResourceObject != null)
            {
                enchantMark = mark.Brush;
                enchantMarkKnown = true;
            }
        }
    }

    /// <summary>The page's widget for a gear slot (Slot_Melee for SW.ItemSlot.Equipment.MeleeWeapon...).</summary>
    static string SlotWidgetName(string slot)
    {
        if (UKismetStringLibrary.Contains(slot, "MeleeWeapon", false, false)) return "Slot_Melee";
        if (UKismetStringLibrary.Contains(slot, "RangedWeapon", false, false)) return "Slot_Ranged";
        if (UKismetStringLibrary.Contains(slot, "Helmet", false, false)) return "Slot_Helmet";
        if (UKismetStringLibrary.Contains(slot, "Chest", false, false)) return "Slot_Chest";
        if (UKismetStringLibrary.Contains(slot, "Leggings", false, false)) return "Slot_Legs";
        if (UKismetStringLibrary.Contains(slot, "Boots", false, false)) return "Slot_Boots";
        string part = Items.LastPart(slot);
        string number = UKismetStringLibrary.Right(part, 1);
        if (UKismetStringLibrary.Contains(slot, "Artifact", false, false)) return "Slot_Artifact" + number;
        if (UKismetStringLibrary.Contains(slot, "Talisman", false, false)) return "Slot_Talisman" + number;
        return "";
    }

    public bool GameFontKnown() => fontObject != null;

    /// <summary>The game's font in one of its typefaces ("TenV2", "Sixteen Semibold", "FiveV2"...) at a size of its units.</summary>
    public FSlateFontInfo GameFont(string typeface, int size) => GameLook.Font(fontObject, typeface, size);

    /// <summary>The item panel's entry box (the box its enchantment sits in).</summary>
    public bool EntryBrush(out FSlateBrush brush)
    {
        brush = entryBrush;
        return entryKnown;
    }

    /// <summary>
    /// The background the page's gear slots have for a rarity: the very one of a slot with gear of that rarity on, else one
    /// made from the same material with the rarity's colour (found in game: TagColor, and StateTalisman for talismans).
    /// </summary>
    public bool RarityBrush(string rarity, out FSlateBrush brush)
    {
        for (int i = 0; i < rarityNames.Count; i++)
        {
            if (rarityNames[i] != rarity) continue;
            brush = rarityBrushes[i];
            return true;
        }
        for (int i = 0; i < madeRarities.Count; i++)
        {
            if (madeRarities[i] != rarity) continue;
            brush = madeBrushes[i];
            return true;
        }
        brush = rarityTemplate;
        if (rarityMaterial == null) return false;
        var material = UKismetMaterialLibrary.CreateDynamicMaterialInstance(this, rarityMaterial, "None", EMIDCreationFlags.Transient);
        if (material == null) return false;
        bool talisman = rarity == "SW.Rarity.None";
        material.SetVectorParameterValue("TagColor", RarityTagColour(rarity));
        material.SetScalarParameterValue("StateTalisman", talisman ? 1 : 0);
        material.SetScalarParameterValue("StateStormItem", 0);
        material.SetScalarParameterValue("EffectLevelSelection", rarity == "SW.Rarity.Unique" ? 3 : rarity == "SW.Rarity.Common" ? 1 : 2);
        brush.ResourceObject = material;
        madeRarities.Add(rarity);
        madeBrushes.Add(brush);
        return true;
    }

    /// <summary>A rarity's colour in the slots' material (unique and special as found in game; the others as they look).</summary>
    static FLinearColor RarityTagColour(string rarity)
    {
        if (rarity == "SW.Rarity.Unique") return Ui.Color(1, 0.21f, 0.03f, 1);
        if (rarity == "SW.Rarity.Special") return Ui.Color(0.03f, 0.36f, 0.99f, 1);
        if (rarity == "SW.Rarity.Rare") return Ui.Color(0.07f, 0.54f, 0.21f, 1);
        if (rarity == "SW.Rarity.None") return Ui.Color(0.01f, 0.01f, 0.01f, 1);
        return Ui.Color(0.4f, 0.4f, 0.42f, 1);
    }

    /// <summary>The mark the page's slots put on enchanted gear (the enchantment swirl).</summary>
    public bool EnchantMark(out FSlateBrush brush)
    {
        brush = enchantMark;
        return enchantMarkKnown;
    }

    bool dumpedStyles;

    /// <summary>Logs how the Effects &amp; Enchantments panel is drawn (once), to build the drawer from the same pieces.</summary>
    void DumpStyles()
    {
        dumpedStyles = true;
        var classic = page as UAS_PlayerInventory;
        var revamp = page as UAS_PlayerInventoryRevamp;
        UWidget? effects = classic != null ? classic.TotalEffectsPanel : revamp != null ? revamp.TotalEffectsPanel : null;
        StyleDump.Write(effects, "style: the Effects & Enchantments panel", 400);
    }

    /// <summary>The loadout the drawer starts on: the last one used, else the one worn, else the first.</summary>
    int StartingLoadout()
    {
        if (HasLoadout(lastLoadout) && lastLoadout < Count) return lastLoadout;
        for (int i = 0; i < Count; i++)
            if (IsWorn(i)) return i;
        return 0;
    }

    /// <summary>
    /// Where the drawer goes: over the item panel under Effects &amp; Stats (it's made like that panel), else below the
    /// button. Absolute coordinates.
    /// </summary>
    void MeasureDrawer()
    {
        drawerRectKnown = false;
        if (!StatsRect(out FVector2D statsTopLeft, out FVector2D statsBottomRight)) return;
        itemPanelAt = -100;
        double right = ColumnRight(World.RealTime(this), statsTopLeft, statsBottomRight);
        if (itemPanelKnown)
        {
            drawerTopLeft = itemPanelTopLeft;
            drawerBottomRight = itemPanelBottomRight;
        }
        else
        {
            double height = statsBottomRight.Y - statsTopLeft.Y;
            drawerTopLeft = Vec2(statsTopLeft.X, statsBottomRight.Y + height * 0.5);
            drawerBottomRight = Vec2(right, statsBottomRight.Y + height * 12);
        }
        drawerRectKnown = true;
    }

    /// <summary>Where the drawer goes, in absolute coordinates; false when it couldn't be measured.</summary>
    public bool DrawerRect(out FVector2D topLeft, out FVector2D bottomRight)
    {
        topLeft = drawerTopLeft;
        bottomRight = drawerBottomRight;
        return drawerRectKnown;
    }

    static FVector2D Vec2(double x, double y) => new FVector2D { X = x, Y = y };

    void LogPage(UAS_PlayerInventoryBase inventoryPage, UCommonButtonBase button, string switchRow)
    {
        var lines = new List<string>();
        lines.Add($"inventory page {GameUI.ClassPath(inventoryPage)}: Effects & Stats is {UKismetSystemLibrary.GetObjectName(button)} ({GameUI.ClassPath(button)}), \"{ButtonLabel(button)}\", action {button.TriggeringInputAction.RowName} (switch {switchRow})");
        Log.WriteAll(lines);
    }

    // ---------------------------------------------------------------- The character and their loadouts

    /// <summary>The player's inventory: on their character, else on their player state.</summary>
    public UInventoryManagerComponent? Inventory()
    {
        var player = World.Player(this);
        UInventoryManagerComponent? inventory = player != null ? UInventoryManagerComponent.GetComponent(player) : null;
        if (inventory != null) return inventory;
        var controller = World.PlayerController(this);
        var state = controller != null ? controller.PlayerState : null;
        return state != null ? UInventoryManagerComponent.GetComponent(state) : null;
    }

    string CharacterId()
    {
        var controller = World.PlayerController(this);
        var state = controller != null ? controller.PlayerState as ABasePlayerState : null;
        string id = state != null ? state.ActiveCharacterId : "";
        return UKismetStringLibrary.Len(id) > 0 ? id : "default";
    }

    /// <summary>Loads the character's loadouts again if their id has only just come through.</summary>
    void CheckCharacter()
    {
        string id = CharacterId();
        if (id == character) return;
        if (Debug) Log.Write($"character changed: {character} -> {id}");
        character = id;
        LoadLoadouts();
    }

    string SaveName() => "loadouts-" + character;

    void LoadLoadouts()
    {
        if (!ModSave.Load(SaveName(), out SavedLoadouts data)) data = new SavedLoadouts { Loadouts = new List<SavedLoadout>() };
        while (data.Loadouts.Count < LoadoutDrawer.Max) data.Loadouts.Add(new SavedLoadout { Used = false, Items = new List<SavedItem>(), Name = "" });
        saved = data;
        Revision++;
    }

    void WriteLoadouts()
    {
        ModSave.Save(SaveName(), saved);
        Revision++;
    }

    /// <summary>A loadout's name: the one the player gave it, else "Loadout 1"...</summary>
    public string LoadoutName(int index)
    {
        string name = index >= 0 && index < saved.Loadouts.Count ? saved.Loadouts[index].Name : "";
        return UKismetStringLibrary.Len(UKismetStringLibrary.Trim(name)) > 0 ? name : $"Loadout {index + 1}";
    }

    public bool HasLoadout(int index) => index >= 0 && index < saved.Loadouts.Count && saved.Loadouts[index].Used;

    /// <summary>A loadout's gear (none for an empty one).</summary>
    public List<SavedItem> LoadoutItems(int index) => HasLoadout(index) ? saved.Loadouts[index].Items : new List<SavedItem>();

    /// <summary>Names a loadout (an empty name goes back to "Loadout 1"...). Names are kept short, like the game's.</summary>
    public void RenameLoadout(int index, string name)
    {
        if (index < 0 || index >= saved.Loadouts.Count) return;
        name = UKismetStringLibrary.Trim(UKismetStringLibrary.TrimTrailing(name));
        if (UKismetStringLibrary.Len(name) > 24) name = UKismetStringLibrary.Left(name, 24);
        var loadout = saved.Loadouts[index];
        loadout.Name = name;
        saved.Loadouts[index] = loadout;
        WriteLoadouts();
        ShowStatus($"Renamed it {LoadoutName(index)}.");
        Sounds.Confirm(this);
    }

    /// <summary>Empties a loadout (its name stays).</summary>
    public void ClearLoadout(int index)
    {
        if (!HasLoadout(index) || Busy()) return;
        var loadout = saved.Loadouts[index];
        loadout.Used = false;
        loadout.Items = new List<SavedItem>();
        saved.Loadouts[index] = loadout;
        WriteLoadouts();
        if (lastLoadout == index) lastLoadout = -1;
        ShowStatus($"Cleared {LoadoutName(index)}.");
        Sounds.Confirm(this);
    }

    /// <summary>Shows a line in the drawer (no toast).</summary>
    public void ShowStatus(string text) => Status = text;

    /// <summary>The gear the character has on: everything equipped but cosmetics.</summary>
    List<FInventoryEntry> Worn(UInventoryManagerComponent inventory)
    {
        var worn = new List<FInventoryEntry>();
        foreach (var entry in inventory.GetEquippedItems())
        {
            if (Items.IsNone(Items.TagName(entry.EquippedSlot))) continue;
            if (UInventoryHelperLibrary.IsCosmetic(this, entry.ItemData)) continue;
            worn.Add(entry);
        }
        return worn;
    }

    static bool WornIn(List<FInventoryEntry> worn, SavedItem item)
    {
        foreach (var entry in worn)
            if (Items.Same(entry.ItemData, item) && Items.TagName(entry.EquippedSlot) == item.Slot) return true;
        return false;
    }

    /// <summary>Whether all of a loadout's gear is on, in its slots.</summary>
    public bool IsWorn(int index)
    {
        if (!HasLoadout(index)) return false;
        var inventory = Inventory();
        if (inventory == null) return false;
        var worn = Worn(inventory);
        foreach (var item in saved.Loadouts[index].Items)
            if (!WornIn(worn, item)) return false;
        return true;
    }

    /// <summary>A saved item in the character's inventory now.</summary>
    static bool FindItem(UInventoryManagerComponent inventory, SavedItem item, out FInventoryEntry found)
    {
        foreach (var entry in inventory.GetAllItems())
        {
            if (!Items.Same(entry.ItemData, item)) continue;
            found = entry;
            return true;
        }
        found = new FInventoryEntry();
        return false;
    }

    /// <summary>A saved item among some entries (e.g. the gear worn now), in whatever slot.</summary>
    static bool FindIn(List<FInventoryEntry> entries, SavedItem item, out FInventoryEntry found)
    {
        foreach (var entry in entries)
        {
            if (!Items.Same(entry.ItemData, item)) continue;
            found = entry;
            return true;
        }
        found = new FInventoryEntry();
        return false;
    }

    /// <summary>
    /// Whether an item's enchantment needs work to be as saved: another enchantment (or none), or fewer points. One
    /// with more points than saved (upgraded since) is left as it is.
    /// </summary>
    static bool NeedsEnchant(FItemData item, SavedItem saved)
    {
        if (Items.IsNone(saved.Enchant)) return false;
        return Items.Enchantment(item) != saved.Enchant || Items.Points(item) < saved.Points;
    }

    /// <summary>
    /// What switching to a loadout would change: how many items go on, and how many enchantments get done. Gear that's
    /// already on, enchanted as saved, is left alone.
    /// </summary>
    void Changes(int index, out int items, out int enchants)
    {
        items = 0;
        enchants = 0;
        var inventory = Inventory();
        if (inventory == null || !HasLoadout(index)) return;
        var worn = Worn(inventory);
        foreach (var item in saved.Loadouts[index].Items)
        {
            if (!WornIn(worn, item)) items++;
            if (!MoveEnchants || Items.IsNone(item.Enchant)) continue;
            bool found = FindIn(worn, item, out FInventoryEntry entry);
            if (!found) found = FindItem(inventory, item, out entry);
            if (found && NeedsEnchant(entry.ItemData, item)) enchants++;
        }
    }

    /// <summary>A loadout's state on its card: "EQUIPPED", or what switching to it would change.</summary>
    public string StateText(int index)
    {
        if (!HasLoadout(index)) return "";
        Changes(index, out int items, out int enchants);
        if (items == 0 && enchants == 0) return "EQUIPPED";
        // Short, to fit beside the name: "SWAP 1 ITEM, 2 ENCHANTS".
        string text = items > 0 ? $"{items} item{(items == 1 ? "" : "s")}" : "";
        if (enchants > 0)
        {
            if (UKismetStringLibrary.Len(text) > 0) text += ", ";
            text += $"{enchants} enchant{(enchants == 1 ? "" : "s")}";
        }
        return "Swap " + text;
    }

    public string GearSummary(int index)
    {
        var items = saved.Loadouts[index].Items;
        string text = "";
        for (int i = 0; i < items.Count && i < 6; i++)
        {
            if (i > 0) text += ",  ";
            text += items[i].Name;
        }
        if (items.Count > 6) text += $",  and {items.Count - 6} more";
        return text;
    }

    public string EnchantSummary(int index)
    {
        string text = "";
        foreach (var item in saved.Loadouts[index].Items)
        {
            if (Items.IsNone(item.Enchant)) continue;
            if (UKismetStringLibrary.Len(text) > 0) text += ",  ";
            text += $"{item.EnchantName} on {item.Name}";
        }
        return UKismetStringLibrary.Len(text) > 0 ? "Enchanted: " + text : "No enchantments";
    }

    /// <summary>Saves the gear the character has on (and its enchantments) as a loadout.</summary>
    public void SaveLoadout(int index)
    {
        CheckCharacter();
        if (Busy())
        {
            SetStatus("Still switching loadouts: try again in a moment.");
            return;
        }
        var inventory = Inventory();
        if (inventory == null)
        {
            SetStatus("Couldn't find your inventory.");
            return;
        }
        var items = new List<SavedItem>();
        var lines = new List<string>();
        foreach (var entry in Worn(inventory))
        {
            var item = Items.Describe(this, entry);
            items.Add(item);
            lines.Add($"  {item.Slot}: {item.Name} ({item.Type}, picked {item.Picked}, {item.Rarity}, power {item.Power:0.0}), enchant {item.Enchant} / {item.EnchantSpecific}, {item.Points} points");
        }
        if (items.Count == 0)
        {
            SetStatus("You have no gear on to save.");
            Sounds.Forbidden(this);
            return;
        }
        saved.Loadouts[index] = new SavedLoadout { Used = true, Items = items, Name = saved.Loadouts[index].Name };
        bool written = ModSave.Save(SaveName(), saved);
        Revision++;
        lastLoadout = index;
        if (Debug)
        {
            lines.Insert(0, $"saved {LoadoutName(index)} ({items.Count} items, {(written ? "written" : "NOT written")}):");
            Log.WriteAll(lines);
        }
        SetStatus($"Saved the gear you have on as {LoadoutName(index)}.");
        Sounds.Confirm(this);
    }

    // ---------------------------------------------------------------- Switching

    public bool Busy() => switching >= 0;

    void NextLoadout()
    {
        int start = lastLoadout;
        if (start < 0)
            for (int i = 0; i < Count; i++)
                if (start < 0 && IsWorn(i)) start = i;
        for (int k = 1; k <= Count; k++)
        {
            int i = ((start < 0 ? -1 : start) + k + Count) % Count;
            if (!HasLoadout(i)) continue;
            EquipLoadout(i);
            return;
        }
        SetStatus("No loadouts saved yet: open the loadouts and save your gear first.");
    }

    /// <summary>
    /// Switches to a loadout: with enchantments moved, first disenchants the gear coming off (only gear a loadout can
    /// enchant again), then equips the loadout's gear, then enchants it as it was saved. One step at a time.
    /// </summary>
    public void EquipLoadout(int index)
    {
        CheckCharacter();
        if (Busy())
        {
            SetStatus("Still switching loadouts: a moment.");
            return;
        }
        if (!HasLoadout(index))
        {
            SetStatus($"{LoadoutName(index)} is empty: Save puts the gear you have on into it.");
            Sounds.Forbidden(this);
            return;
        }
        // In someone else's game, the gear is theirs to change (the game's equip and enchant calls don't come through):
        // say so rather than start a swap that can't finish.
        var host = World.Player(this);
        if (host != null && !host.HasAuthority())
        {
            SetStatus("Loadouts can only swap gear in a game you host, not when you've joined someone else's. Saving and renaming still work.");
            Sounds.Forbidden(this);
            return;
        }
        var player = World.Player(this);
        var inventory = Inventory();
        if (player == null || inventory == null)
        {
            SetStatus("Couldn't find your inventory.");
            return;
        }
        var worn = Worn(inventory);
        // The loadout being left keeps its gear's enchantments as they are now (e.g. upgraded since it was saved), so
        // they come back like that.
        RememberEnchantments(worn, index);
        var loadout = saved.Loadouts[index];
        stepKinds = new List<int>();
        stepItems = new List<SavedItem>();
        var plan = new List<string>();
        // Only what's different: gear staying on keeps its enchantment, gear already in its slot isn't touched, and
        // enchantments already as saved aren't redone.
        if (MoveEnchants)
        {
            foreach (var entry in worn)
            {
                if (InLoadout(loadout, entry.ItemData)) continue;
                if (!UInventoryHelperLibrary.IsItemEnchanted(entry.ItemData)) continue;
                var leaving = Items.Describe(this, entry);
                if (!Restorable(entry.ItemData))
                {
                    plan.Add($"  keep the enchantment of {leaving.Name} ({leaving.Enchant}): no loadout has it like that");
                    continue;
                }
                AddStep(StepDisenchant, leaving);
                plan.Add($"  disenchant {leaving.Name} ({leaving.Enchant}, {leaving.Points} points): coming off");
            }
        }
        foreach (var item in loadout.Items)
        {
            if (WornIn(worn, item)) continue;
            AddStep(StepEquip, item);
            plan.Add($"  equip {item.Name} in {item.Slot}");
        }
        if (MoveEnchants)
        {
            foreach (var item in loadout.Items)
            {
                if (Items.IsNone(item.Enchant)) continue;
                bool found = FindIn(worn, item, out FInventoryEntry entry);
                if (!found) found = FindItem(inventory, item, out entry);
                if (!found || !NeedsEnchant(entry.ItemData, item)) continue;
                AddStep(StepEnchant, item);
                plan.Add($"  enchant {item.Name}: has {Items.Enchantment(entry.ItemData)} ({Items.Points(entry.ItemData)} points), wants {item.Enchant} ({item.Points} points)");
            }
        }
        if (Debug)
        {
            plan.Insert(0, $"switching to {LoadoutName(index)}: {stepKinds.Count} steps; authority {player.HasAuthority()}");
            Log.WriteAll(plan);
        }
        if (stepKinds.Count == 0)
        {
            lastLoadout = index;
            SetStatus($"You already have {LoadoutName(index)} on, enchanted as saved: nothing to change.");
            Sounds.Confirm(this);
            return;
        }

        switching = index;
        stepAt = 0;
        stepTries = 0;
        nextStepAt = 0;
        equippedCount = 0;
        missingCount = 0;
        missingNames = "";
        failedCount = 0;
        disenchanted = 0;
        enchanted = 0;
        enchantShort = 0;
        enchantFailed = 0;
        stonesBefore = UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.SpringStone);
        pointsBefore = UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.EnchantmentPoint);
        Status = $"Switching to {LoadoutName(index)}...";
    }

    /// <summary>
    /// The loadout being left (the last one switched to or saved, if it's still on) takes its gear's enchantments as
    /// they are now, so upgrades made while wearing it aren't lost when it's put back on. Its gear isn't changed.
    /// </summary>
    void RememberEnchantments(List<FInventoryEntry> worn, int switchingTo)
    {
        if (lastLoadout < 0 || lastLoadout == switchingTo || !HasLoadout(lastLoadout)) return;
        var loadout = saved.Loadouts[lastLoadout];
        var items = loadout.Items;
        bool changed = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!WornIn(worn, item)) continue;
            FindIn(worn, item, out FInventoryEntry entry);
            var now = Items.Describe(this, entry);
            // An enchantment taken off by hand isn't remembered: the loadout still puts its own back.
            if (Items.IsNone(now.Enchant) || (now.Enchant == item.Enchant && now.Points == item.Points)) continue;
            if (Debug) Log.Write($"{LoadoutName(lastLoadout)} remembers {item.Name} as {now.Enchant} ({now.Points} points), was {item.Enchant} ({item.Points} points)");
            item.Enchant = now.Enchant;
            item.EnchantSpecific = now.EnchantSpecific;
            item.Points = now.Points;
            item.EnchantName = now.EnchantName;
            items[i] = item;
            changed = true;
        }
        if (!changed) return;
        loadout.Items = items;
        saved.Loadouts[lastLoadout] = loadout;
        WriteLoadouts();
    }

    void AddStep(int kind, SavedItem item)
    {
        stepKinds.Add(kind);
        stepItems.Add(item);
    }

    static bool InLoadout(SavedLoadout loadout, FItemData item)
    {
        foreach (var saved in loadout.Items)
            if (Items.Same(item, saved)) return true;
        return false;
    }

    /// <summary>Whether a saved loadout has this item with the enchantment it has now (so it can be put back).</summary>
    bool Restorable(FItemData item)
    {
        string enchant = Items.Enchantment(item);
        foreach (var loadout in saved.Loadouts)
        {
            if (!loadout.Used) continue;
            foreach (var savedItem in loadout.Items)
                if (Items.Same(item, savedItem) && savedItem.Enchant == enchant) return true;
        }
        return false;
    }

    void RunStep()
    {
        if (stepAt >= stepKinds.Count)
        {
            FinishSwitch();
            return;
        }
        var player = World.Player(this);
        var inventory = Inventory();
        if (player == null || inventory == null)
        {
            if (Debug) Log.Write("switch: the player or their inventory is gone, stopping");
            FinishSwitch();
            return;
        }
        int kind = stepKinds[stepAt];
        var item = stepItems[stepAt];
        bool done = true;
        if (!FindItem(inventory, item, out FInventoryEntry entry))
        {
            if (kind == StepEquip)
            {
                missingCount++;
                if (UKismetStringLibrary.Len(missingNames) > 0) missingNames += ", ";
                missingNames += item.Name;
            }
            if (Debug) Log.Write($"switch: {item.Name} ({item.Type}, picked {item.Picked}) isn't in the inventory");
        }
        else if (kind == StepDisenchant) Disenchant(player, entry, item);
        else if (kind == StepEquip) EquipItem(player, inventory, entry, item);
        else done = EnchantItem(player, entry, item);
        stepTries++;
        if (done || stepTries >= StepTriesMax)
        {
            stepAt++;
            stepTries = 0;
        }
    }

    void Disenchant(APawn player, FInventoryEntry entry, SavedItem item)
    {
        if (!UInventoryHelperLibrary.IsItemEnchanted(entry.ItemData)) return;
        var refund = UInventoryHelperLibrary.GetDisenchantRefund(player, entry.ItemData);
        Trail.Begin("disenchant");
        bool ok = UInventoryHelperLibrary.TryDisenchantItem(player, entry.ItemData.SessionUID);
        Trail.End("disenchant");
        if (ok) disenchanted++;
        if (Debug) Log.Write($"switch: disenchant {item.Name} ({Items.Enchantment(entry.ItemData)}, refund {Items.Describe(refund)}): {(ok ? "ok" : "FAILED")}");
    }

    void EquipItem(APawn player, UInventoryManagerComponent inventory, FInventoryEntry entry, SavedItem item)
    {
        if (Items.TagName(entry.EquippedSlot) == item.Slot)
        {
            equippedCount++;
            return;
        }
        var slot = GameUI.Tag(item.Slot);
        var uid = entry.ItemData.SessionUID;
        // The game's equip ability's own function first (what equipping from the inventory runs), then the inventory's.
        Trail.Begin("equip");
        UGA_ItemEquip.DoEquip(player, slot, uid);
        Trail.End("equip");
        string how = "DoEquip";
        if (!EquippedIn(inventory, item))
        {
            Trail.Begin("equip (inventory)");
            bool ok = inventory.EquipItem(slot, uid);
            Trail.End("equip (inventory)");
            how = $"EquipItem ({ok})";
        }
        bool equipped = EquippedIn(inventory, item);
        if (equipped) equippedCount++;
        else failedCount++;
        if (Debug) Log.Write($"switch: equip {item.Name} in {item.Slot} (was in {entry.EquippedSlot.TagName}): {(equipped ? "ok" : "FAILED")} with {how}");
    }

    static bool EquippedIn(UInventoryManagerComponent inventory, SavedItem item)
    {
        if (!FindItem(inventory, item, out FInventoryEntry now)) return false;
        return Items.TagName(now.EquippedSlot) == item.Slot;
    }

    /// <summary>
    /// Brings an item's enchantment back to the saved one: removes another enchantment, enchants it, then upgrades it
    /// to the saved points. One action per call; true once it's as saved (or can't be).
    /// </summary>
    bool EnchantItem(APawn player, FInventoryEntry entry, SavedItem item)
    {
        var data = entry.ItemData;
        var uid = data.SessionUID;
        string current = Items.Enchantment(data);
        if (!Items.IsNone(current) && current != item.Enchant)
        {
            // Another enchantment: off with it first (its points come back).
            bool removed = UInventoryHelperLibrary.TryDisenchantItem(player, uid);
            if (removed) disenchanted++;
            if (Debug) Log.Write($"switch: {item.Name} has {current}, not {item.Enchant}: disenchant {(removed ? "ok" : "FAILED")}");
            return !removed;
        }
        if (Items.IsNone(current))
        {
            bool ok = Enchant(player, entry, item);
            if (ok) enchanted++;
            // Next time round: its points.
            return !ok;
        }
        // The saved enchantment: upgrade it to the saved points.
        int points = Items.Points(data);
        if (points >= item.Points) return true;
        var reasons = UGA_ItemEnchantUpgrade.CanEnchantUpgrade(player, uid);
        if (reasons.Count > 0)
        {
            string why = "";
            foreach (var reason in reasons) why += $" {reason}";
            if (reasons.Contains(EGA_ItemEnchantUpgrade_FailReason.Cost)) enchantShort++;
            else enchantFailed++;
            if (Debug) Log.Write($"switch: can't upgrade {item.Name}'s {item.Enchant} ({points} of {item.Points} points):{why}");
            return true;
        }
        Trail.Begin("upgrade enchantment");
        bool upgraded = UGA_ItemEnchantUpgrade.DoEnchantUpgrade(player, uid);
        Trail.End("upgrade enchantment");
        if (Debug) Log.Write($"switch: upgrade {item.Name}'s {item.Enchant} from {points} points: {(upgraded ? "ok" : "FAILED")}");
        return !upgraded;
    }

    /// <summary>
    /// Enchants an item with its saved enchantment as the Enchantsmith does: the character applies the enchantment's
    /// first tier (SW.Enchantment.Swirling.I, found in game) in the item's slot, and upgrades bring it to its saved tier.
    /// Enchantments without tiers are tried by their saved tags, then by the matching ones the character owns.
    /// </summary>
    bool Enchant(APawn player, FInventoryEntry entry, SavedItem item)
    {
        var data = entry.ItemData;
        string slotName = Items.TagName(entry.EquippedSlot);
        var slot = GameUI.Tag(Items.IsNone(slotName) ? item.Slot : slotName);
        enchantWasShort = false;
        enchantWhy = "";
        var tags = new List<string>();
        AddTag(tags, item.Enchant + ".I");
        AddTag(tags, item.EnchantSpecific);
        AddTag(tags, item.Enchant);
        if (EnchantWith(player, data, slot, tags, item)) return true;
        var owned = new List<string>();
        foreach (var pair in UInventoryHelperLibrary.GetValidOwnedEnchantmentsForItem(player, data))
        {
            string key = Items.TagName(pair.Key);
            if (!tags.Contains(key) && UKismetStringLibrary.StartsWith(key, item.Enchant, ESearchCase.IgnoreCase)) AddTag(owned, key);
        }
        if (owned.Count > 0 && EnchantWith(player, data, slot, owned, item)) return true;
        if (enchantWasShort) enchantShort++;
        else enchantFailed++;
        if (Debug) Log.Write($"switch: can't enchant {item.Name} with {item.Enchant} in {slot.TagName}:{enchantWhy}");
        return false;
    }

    /// <summary>Applies the first of some enchantment tags the game allows on the item. Why the others weren't goes to enchantWhy.</summary>
    bool EnchantWith(APawn player, FItemData data, FGameplayTag slot, List<string> tags, SavedItem item)
    {
        foreach (var name in tags)
        {
            var tag = GameUI.Tag(name);
            var reasons = UGA_ItemEnchant.CanEnchant(player, data.SessionUID, slot, tag);
            if (reasons.Count > 0)
            {
                enchantWhy += $" [{name}:";
                foreach (var reason in reasons) enchantWhy += $" {reason}";
                enchantWhy += "]";
                if (reasons.Contains(EGA_ItemEnchant_FailReason.Cost)) enchantWasShort = true;
                continue;
            }
            Trail.Begin("enchant");
            bool ok = UGA_ItemEnchant.DoEnchant(player, data.SessionUID, slot, tag);
            Trail.End("enchant");
            if (Debug) Log.Write($"switch: enchant {item.Name} with {name} in {slot.TagName}: {(ok ? "ok" : "FAILED")}");
            if (ok) return true;
            enchantWhy += $" [{name}: allowed, but it didn't take]";
        }
        return false;
    }

    static void AddTag(List<string> tags, string tag)
    {
        if (Items.IsNone(tag) || tags.Contains(tag)) return;
        tags.Add(tag);
    }

    void FinishSwitch()
    {
        var player = World.Player(this);
        int stones = player != null ? UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.SpringStone) : stonesBefore;
        int points = player != null ? UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.EnchantmentPoint) : pointsBefore;
        string name = LoadoutName(switching);
        bool complete = missingCount == 0 && failedCount == 0;
        string text = complete ? $"Equipped {name}." : $"Equipped {name}, but not all of it.";
        if (missingCount > 0) text += $" Not in your inventory any more: {missingNames}.";
        if (failedCount > 0) text += $" {failedCount} couldn't be equipped.";
        if (enchanted > 0 || disenchanted > 0)
        {
            text += $" Enchantments: {disenchanted} removed, {enchanted} applied";
            int spent = stonesBefore - stones;
            text += spent > 0 ? $" ({spent} Spring Stones)." : ".";
        }
        if (enchantShort > 0) text += $" Not enough to enchant {enchantShort} of them.";
        if (enchantFailed > 0) text += $" {enchantFailed} enchantments failed.";
        if (Debug) Log.Write($"switch done: {equippedCount} equipped, {missingCount} missing, {failedCount} failed; spring stones {stonesBefore} -> {stones}, enchantment points {pointsBefore} -> {points}");
        lastLoadout = switching;
        Revision++;
        switching = -1;
        SetStatus(text);
        if (complete && enchantShort == 0 && enchantFailed == 0) Sounds.Confirm(this);
        else Sounds.Forbidden(this);
    }

    void SetStatus(string text)
    {
        Status = text;
        // Shown in the drawer; without it, as a toast.
        if (!DrawerOpen()) GameUI.Toast(this, text, 4f);
        if (Debug) Log.Write("status: " + text);
    }

    // ---------------------------------------------------------------- Diagnostics

    void LogStart(APawn player, UInventoryManagerComponent inventory)
    {
        var lines = new List<string>();
        var owner = inventory.GetOwner();
        lines.Add($"level {World.LevelName(this)}: character {character}, inventory on {GameUI.ClassPath(owner)}");
        lines.Add($"  authority {player.HasAuthority()}, server {UKismetSystemLibrary.IsServer(this)}, standalone {UKismetSystemLibrary.IsStandalone(this)}");
        lines.Add($"  spring stones {UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.SpringStone)}, enchantment points {UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.EnchantmentPoint)}, emeralds {UInventoryHelperLibrary.GetPlayerCurrency(player, ECurrency.Emerald)}");
        foreach (var entry in inventory.GetEquippedItems())
        {
            var item = Items.Describe(this, entry);
            bool cosmetic = UInventoryHelperLibrary.IsCosmetic(this, entry.ItemData);
            lines.Add($"  worn {item.Slot}: {item.Name} ({item.Type}, picked {item.Picked}, {item.Rarity}, power {item.Power:0.0}){(cosmetic ? " cosmetic" : "")}, enchant {item.Enchant} / {item.EnchantSpecific}, {item.Points} points");
        }
        lines.Add($"  {inventory.GetAllItems().Count} items in all, {UInventoryHelperLibrary.GetAllOwnedEnchantmentBooks(player).Count} enchantment books");
        for (int i = 0; i < saved.Loadouts.Count; i++)
            if (saved.Loadouts[i].Used) lines.Add($"  loadout {i + 1} ({LoadoutName(i)}): {saved.Loadouts[i].Items.Count} items{(IsWorn(i) ? ", worn" : "")}");
        Log.WriteAll(lines);
    }
}
