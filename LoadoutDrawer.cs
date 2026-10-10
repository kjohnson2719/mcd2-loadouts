using System.Collections.Generic;
using ModKit;
using NeoRune;
using UE.Angelscript;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Engine;
using UE.InputCore;
using UE.InventorySystem;
using UE.SlateCore;
using UE.UMG;

namespace Loadouts;

/// <summary>
/// The loadouts drawer, made of the game's own pieces so it looks like the inventory page's panels: the item panel's
/// header ribbon (the game's "Equipped" module, saying "Loadouts") over the game's tooltip panel (its frame, blur and
/// button bar), laid out at the game's sizes with its fonts. Inside: the loadouts as the game's entry boxes, the selected
/// one's gear in the game's rarity frames, its enchantments, and the actions in the panel's button bar. It covers the item
/// panel and slides in over it.
/// It's on the screen over the page, not one of the game's screens: found in game (1.1), the inventory page keeps taking
/// the game's back and focus while it shows, so a screen of ours over it got stuck. Its keys come from the mod's key
/// tracker instead (<see cref="Key"/>), which keeps them from the page, and the mouse clicks it as usual.
/// </summary>
public class LoadoutDrawer : ModWidget
{
    public const int Max = 6;
    const int MaxName = 24;
    const int IconCount = 12;
    const int IconsPerRow = 6;
    const float IconSize = 140;
    const float IconGap = 16;
    const double SlideSeconds = 0.18;
    // The actions, in the order of the hints in the button bar.
    const int ActionEquip = 0;
    const int ActionSave = 1;
    const int ActionRename = 2;
    const int ActionClear = 3;
    const int ActionBack = 4;

    ModActor? mod;
    UWidget? drawer;
    UScaleBox? scaler;
    USizeBox? sizer;
    UUserWidget? ribbon;
    UTextBlock? ribbonText;
    List<UButton> rows;
    List<UTextBlock> rowNames;
    List<UTextBlock> rowStates;
    List<UImage> icons;
    List<UImage> iconFrames;
    List<UImage> iconMarks;
    double iconSizeShown;
    List<USizeBox> iconSlots;
    UVerticalBox? enchantList;
    UTextBlock? status;
    UBorder? renameBox;
    UBorder? renameSelection;
    UTextBlock? renameText;
    string typed;
    // The old name, selected, until a key is typed: then it's replaced (Backspace clears it).
    bool replacing;
    double typedAt;
    List<UButton> actionButtons;
    List<UTextBlock> actionTexts;
    List<UCommonActionWidget> actionIcons;
    List<UBorder> actionKeyCaps;
    List<UTextBlock> actionKeyNames;
    List<string> shownRows;
    // Every text and its typeface and size, given the game's font once the mod has it.
    List<UTextBlock> texts;
    List<string> textFaces;
    List<int> textSizes;
    bool styled;
    int selected;
    int renaming;
    int iconsShownFor;
    int iconsRevision;
    double openedAt;
    double lastFill;
    int confirmAction;
    int confirmIndex;
    double confirmAt;
    bool loggedTree;

    protected override UWidget? Build(UWidgetTree tree)
    {
        // Widgets can't have field initializers (NeoRune): everything starts here.
        rows = new List<UButton>();
        rowNames = new List<UTextBlock>();
        rowStates = new List<UTextBlock>();
        icons = new List<UImage>();
        iconFrames = new List<UImage>();
        iconMarks = new List<UImage>();
        iconSizeShown = -1;
        iconSlots = new List<USizeBox>();
        actionButtons = new List<UButton>();
        actionTexts = new List<UTextBlock>();
        actionIcons = new List<UCommonActionWidget>();
        actionKeyCaps = new List<UBorder>();
        actionKeyNames = new List<UTextBlock>();
        shownRows = new List<string>();
        texts = new List<UTextBlock>();
        textFaces = new List<string>();
        textSizes = new List<int>();
        renaming = -1;
        typed = "";
        iconsShownFor = -1;
        iconsRevision = -1;
        openedAt = -1;
        lastFill = -100;
        confirmAction = -1;

        var canvas = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCanvasPanel>(), tree) as UCanvasPanel;
        if (canvas == null) return null;
        // A click outside the drawer closes it, like the game's own side panels.
        var away = UGameplayStatics.SpawnObject(Unreal.ClassOf<UButton>(), tree) as UButton;
        if (away != null)
        {
            away.SetBackgroundColor(Ui.Color(0, 0, 0, 0));
            away.IsFocusable = false;
            away.OnClicked += OnClickAway;
            var awaySlot = canvas.AddChildToCanvas(away);
            awaySlot?.SetAnchors(new UE.Slate.FAnchors { Minimum = Vec2(0, 0), Maximum = Vec2(1, 1) });
            awaySlot?.SetOffsets(Ui.Margin(0));
        }

        // Laid out in the game's own units (its 4K design sizes), scaled like the inventory page.
        scaler = UGameplayStatics.SpawnObject(Unreal.ClassOf<UScaleBox>(), tree) as UScaleBox;
        sizer = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (scaler == null || sizer == null) return canvas;
        scaler.SetStretch(UE.Slate.EStretch.UserSpecified);
        scaler.SetUserSpecifiedScale(0.5f);
        sizer.SetWidthOverride(1500);
        sizer.SetHeightOverride(1700);
        scaler.AddChild(sizer);

        // The loadouts in the panel's upper part, the selected loadout's gear and enchantments in its scrolling lower part,
        // and the actions in its button bar.
        var upper = Ui.Column(tree);
        var lower = Ui.Column(tree);
        var actions = Ui.Row(tree);
        if (upper == null || lower == null || actions == null) return canvas;
        for (int i = 0; i < Max; i++)
        {
            var row = Row(tree, i);
            if (row == null) continue;
            var slot = upper.AddChild(row) as UVerticalBoxSlot;
            slot?.SetPadding(new FMargin { Bottom = 8 });
        }
        BuildLower(tree, lower);
        AddAction(tree, actions, ActionEquip, "Equip");
        AddAction(tree, actions, ActionSave, "Save");
        AddAction(tree, actions, ActionRename, "Rename");
        AddAction(tree, actions, ActionClear, "Clear");
        var bar = Ui.Panel(tree, actions, Ui.Color(0, 0, 0, 0), 0);
        bar?.SetPadding(new FMargin { Left = 40, Right = 40, Top = 24, Bottom = 24 });
        bar?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Right);

        // The game's item panel (the kit's GamePanel): its ribbon, retitled, over its tooltip panel.
        var panel = upper != null && lower != null ? GamePanel.Build(this, tree, "Loadouts", upper, lower, bar, out ribbonText, out ribbon) : null;
        if (panel != null) sizer.AddChild(panel);
        else
        {
            // Without the game's panel (a game update): a plain one.
            var plain = Ui.Column(tree);
            if (plain != null)
            {
                var title = Label(tree, "Loadouts", GameLook.TitleFace, 39, Ui.Color(1, 1, 1, 1));
                var header = Ui.Panel(tree, Centred(tree, title), Ui.Color(0.3f, 0.44f, 0.52f, 1), 16);
                if (header != null) plain.AddChild(header);
                plain.AddChild(upper);
                var lowerSlot = plain.AddChild(lower) as UVerticalBoxSlot;
                lowerSlot?.SetSize(new FSlateChildSize { Value = 1, SizeRule = ESlateSizeRule.Fill });
                if (bar != null) plain.AddChild(bar);
            }
            var box = Ui.Panel(tree, plain, Ui.Color(0.045f, 0.075f, 0.1f, 0.97f), 80);
            if (box != null) sizer.AddChild(box);
        }

        drawer = scaler;
        var drawerSlot = canvas.AddChildToCanvas(scaler);
        // Off screen until it's placed (the first frame), sized by its content.
        drawerSlot?.SetAutoSize(true);
        drawerSlot?.SetPosition(Vec2(-10000, 0));
        return canvas;
    }

    /// <summary>The lower part: the selected loadout's gear (in the game's rarity frames), its enchantments, renaming, status.</summary>
    void BuildLower(UWidgetTree tree, UVerticalBox lower)
    {
        AddHeader(tree, lower, "Gear");
        for (int line = 0; line < IconCount / IconsPerRow; line++)
        {
            var iconRow = Ui.Row(tree);
            if (iconRow == null) continue;
            for (int k = 0; k < IconsPerRow; k++) AddIcon(tree, iconRow);
            var lineSlot = lower.AddChild(iconRow) as UVerticalBoxSlot;
            lineSlot?.SetPadding(new FMargin { Bottom = 16 });
        }
        AddHeader(tree, lower, "Enchantments");
        enchantList = Ui.Column(tree);
        if (enchantList != null) lower.AddChild(enchantList);
        // Renaming: the name being typed (the keys come through the mod's key tracker), shown while it's typed.
        renameText = Label(tree, "", GameLook.TextFace, 45, Ui.Color(1, 1, 1, 1));
        if (renameText != null)
        {
            // The name in a box that's the selection's blue while the old name is selected.
            renameSelection = Ui.Panel(tree, renameText, Ui.Color(0, 0, 0, 0), 0);
            renameSelection?.SetPadding(new FMargin { Left = 4, Right = 4 });
            renameBox = Ui.Panel(tree, renameSelection, Ui.Color(0.16f, 0.25f, 0.31f, 1), 24);
            if (renameBox != null)
            {
                renameBox.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Left);
                renameBox.SetVisibility(ESlateVisibility.Collapsed);
                var slot = lower.AddChild(renameBox) as UVerticalBoxSlot;
                slot?.SetPadding(new FMargin { Top = 24 });
            }
        }
        status = Label(tree, "", GameLook.TextFace, 36, Ui.Color(0.84f, 0.87f, 0.95f, 1));
        if (status != null)
        {
            status.SetAutoWrapText(true);
            var slot = lower.AddChild(status) as UVerticalBoxSlot;
            slot?.SetPadding(new FMargin { Top = 28 });
        }
    }

    /// <summary>A section's header, like the game's ("Effects" in the item panel).</summary>
    void AddHeader(UWidgetTree tree, UVerticalBox box, string text)
    {
        var header = Label(tree, text, GameLook.TextFace, 45, Ui.Color(0.57f, 0.59f, 0.65f, 1));
        if (header == null) return;
        var slot = box.AddChild(header) as UVerticalBoxSlot;
        slot?.SetPadding(new FMargin { Top = 20, Bottom = 16 });
    }

    /// <summary>A loadout's row: an entry box like the game's, its name, and its state at the right.</summary>
    UButton? Row(UWidgetTree tree, int index)
    {
        var button = UGameplayStatics.SpawnObject(Unreal.ClassOf<UButton>(), tree) as UButton;
        var name = Label(tree, $"Loadout {index + 1}", GameLook.TitleFace, 45, Ui.Color(1, 1, 1, 1));
        var state = Label(tree, "", GameLook.TagFace, 29, Ui.Color(0.57f, 0.59f, 0.65f, 1));
        var line = Ui.Row(tree);
        if (button == null || name == null || state == null || line == null) return null;
        var nameSlot = line.AddChild(name) as UHorizontalBoxSlot;
        nameSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        var stateSlot = line.AddChild(state) as UHorizontalBoxSlot;
        stateSlot?.SetSize(new FSlateChildSize { Value = 1, SizeRule = ESlateSizeRule.Fill });
        stateSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Right);
        stateSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        stateSlot?.SetPadding(new FMargin { Left = 24 });
        state.SetJustification(UE.Slate.ETextJustify.Right);
        state.SetAutoWrapText(true);
        button.AddChild(line);
        // Only the mouse clicks the rows: the keys come through the mod's key tracker, and focus stays with the page.
        button.IsFocusable = false;
        var contentSlot = line.Slot as UButtonSlot;
        contentSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Fill);
        contentSlot?.SetPadding(new FMargin { Left = 32, Right = 32, Top = 10, Bottom = 10 });
        if (index == 0) button.OnClicked += Row0;
        else if (index == 1) button.OnClicked += Row1;
        else if (index == 2) button.OnClicked += Row2;
        else if (index == 3) button.OnClicked += Row3;
        else if (index == 4) button.OnClicked += Row4;
        else button.OnClicked += Row5;
        button.OnHovered += HoverSound;
        rows.Add(button);
        rowNames.Add(name);
        rowStates.Add(state);
        return button;
    }

    /// <summary>
    /// An item slot like the game's: its background and frame for the item's rarity (taken from the page's own slots),
    /// with the item's icon. Each layer fills the slot (overlay slots start at the top left, at the brush's own size).
    /// </summary>
    void AddIcon(UWidgetTree tree, UHorizontalBox line)
    {
        var frame = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        var image = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        var mark = UGameplayStatics.SpawnObject(Unreal.ClassOf<UImage>(), tree) as UImage;
        var overlay = UGameplayStatics.SpawnObject(Unreal.ClassOf<UOverlay>(), tree) as UOverlay;
        var box = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (frame == null || image == null || mark == null || overlay == null || box == null) return;
        box.SetWidthOverride(IconSize);
        box.SetHeightOverride(IconSize);
        Fill(overlay.AddChildToOverlay(frame), 0);
        Fill(overlay.AddChildToOverlay(image), IconSize * 0.14);
        // The enchanted mark, in the top right corner like the game's slots.
        var markSlot = overlay.AddChildToOverlay(mark);
        markSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Right);
        markSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Top);
        markSlot?.SetPadding(Ui.Margin(6));
        mark.SetVisibility(ESlateVisibility.Collapsed);
        box.AddChild(overlay);
        box.SetVisibility(ESlateVisibility.Collapsed);
        var slot = line.AddChild(box) as UHorizontalBoxSlot;
        slot?.SetPadding(new FMargin { Right = IconGap });
        icons.Add(image);
        iconFrames.Add(frame);
        iconMarks.Add(mark);
        iconSlots.Add(box);
    }

    static void Fill(UOverlaySlot? slot, double padding)
    {
        if (slot == null) return;
        slot.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Fill);
        slot.SetVerticalAlignment(EVerticalAlignment.VAlign_Fill);
        slot.SetPadding(Ui.Margin((float)padding));
    }

    /// <summary>The slots' size: as large as the game's (180) while six fit across the panel.</summary>
    void FitIcons(double panelWidth)
    {
        // The panel's padding (80 each side), the scroll bar's room, and a gap after each slot.
        double size = (panelWidth - 200) / IconsPerRow - IconGap;
        if (size > IconSize) size = IconSize;
        if (size < 80) size = 80;
        if (size == iconSizeShown) return;
        iconSizeShown = size;
        // The gear again, with marks to the new size.
        iconsShownFor = -1;
        for (int k = 0; k < iconSlots.Count; k++)
        {
            iconSlots[k].SetWidthOverride((float)size);
            iconSlots[k].SetHeightOverride((float)size);
            var iconSlot = icons[k].Slot as UOverlaySlot;
            iconSlot?.SetPadding(Ui.Margin((float)(size * 0.14)));
        }
    }

    /// <summary>An action's hint, like the game's: its name, then its button's icon (or the key's name on a plate). Clickable.</summary>
    void AddAction(UWidgetTree tree, UHorizontalBox actions, int action, string label)
    {
        var button = UGameplayStatics.SpawnObject(Unreal.ClassOf<UButton>(), tree) as UButton;
        var line = Ui.Row(tree);
        var text = Label(tree, label, GameLook.ButtonFace, 32, Ui.Color(1, 1, 1, 1));
        var icon = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCommonActionWidget>(), tree) as UCommonActionWidget;
        var iconBox = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        var keyName = Label(tree, "", GameLook.ButtonFace, 28, Ui.Color(1, 1, 1, 1));
        var keyCap = Ui.Panel(tree, keyName, Ui.Color(0.02f, 0.03f, 0.04f, 0.9f), 4);
        if (button == null || line == null || text == null || icon == null || iconBox == null || keyName == null || keyCap == null) return;
        var textSlot = line.AddChild(text) as UHorizontalBoxSlot;
        textSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        iconBox.SetWidthOverride(56);
        iconBox.SetHeightOverride(56);
        iconBox.AddChild(icon);
        var iconSlot = line.AddChild(iconBox) as UHorizontalBoxSlot;
        iconSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        iconSlot?.SetPadding(new FMargin { Left = 16 });
        keyCap.SetPadding(new FMargin { Left = 12, Right = 12, Top = 2, Bottom = 2 });
        var capSlot = line.AddChild(keyCap) as UHorizontalBoxSlot;
        capSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        capSlot?.SetPadding(new FMargin { Left = 16 });
        button.AddChild(line);
        button.SetBackgroundColor(Ui.Color(0, 0, 0, 0));
        // The controller uses the buttons on the pad: only the mouse clicks these.
        button.IsFocusable = false;
        if (action == ActionEquip) button.OnClicked += ClickEquip;
        else if (action == ActionSave) button.OnClicked += ClickSave;
        else if (action == ActionRename) button.OnClicked += ClickRename;
        else if (action == ActionClear) button.OnClicked += ClickClear;
        else button.OnClicked += OnClickAway;
        var slot = actions.AddChild(button) as UHorizontalBoxSlot;
        slot?.SetPadding(new FMargin { Left = 32 });
        actionButtons.Add(button);
        actionTexts.Add(text);
        actionIcons.Add(icon);
        actionKeyCaps.Add(keyCap);
        actionKeyNames.Add(keyName);
        shownRows.Add("");
    }

    /// <summary>Text, remembered with its typeface and size so it can take the game's font (<see cref="ApplyStyle"/>).</summary>
    UTextBlock? Label(UWidgetTree tree, string text, string typeface, int size, FLinearColor colour)
    {
        var block = Ui.Text(tree, text, size, colour);
        if (block == null) return null;
        texts.Add(block);
        textFaces.Add(typeface);
        textSizes.Add(size);
        return block;
    }

    static UOverlay? Centred(UWidgetTree tree, UWidget? content)
    {
        var overlay = UGameplayStatics.SpawnObject(Unreal.ClassOf<UOverlay>(), tree) as UOverlay;
        if (overlay == null || content == null) return overlay;
        var slot = overlay.AddChildToOverlay(content);
        slot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Center);
        return overlay;
    }

    /// <summary>
    /// Shows the mod's loadouts, starting on one (the one worn, else the first). The drawer is made once and shown again
    /// each time it opens, so each opening starts afresh.
    /// </summary>
    public void Begin(ModActor owner, int start)
    {
        mod = owner;
        selected = start;
        openedAt = -1;
        lastFill = -100;
        confirmAction = -1;
        iconsShownFor = -1;
        if (renaming >= 0) StopRenaming();
        // Off screen until it's placed, so it doesn't show where it was last time for a frame.
        var slot = drawer != null ? drawer.Slot as UCanvasPanelSlot : null;
        slot?.SetPosition(Vec2(-10000, 0));
        Refresh(-1);
    }

    /// <summary>
    /// The game's look, once the mod has it: its font on every text (in the typeface each was made for), and the entry
    /// boxes' brush on the rows.
    /// </summary>
    void ApplyStyle()
    {
        if (mod == null || styled || !mod.GameFontKnown()) return;
        styled = true;
        for (int i = 0; i < texts.Count; i++)
        {
            texts[i].SetFont(mod.GameFont(textFaces[i], textSizes[i]));
            texts[i].SetShadowOffset(Vec2(0, 4));
            texts[i].SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.5f));
        }
        if (mod.EntryBrush(out FSlateBrush entry))
        {
            for (int i = 0; i < rows.Count; i++) rows[i].SetStyle(RowStyle(entry));
            renameBox?.SetBrush(entry);
        }
    }

    /// <summary>A row's look: the game's entry box (the item panel's enchantment box), lighter when hovered or pressed.</summary>
    static FButtonStyle RowStyle(FSlateBrush entry)
    {
        var style = new FButtonStyle();
        style.Normal = Tinted(entry, Ui.Color(1, 1, 1, 0.09f));
        style.Hovered = Tinted(entry, Ui.Color(1, 1, 1, 0.18f));
        style.Pressed = Tinted(entry, Ui.Color(1, 1, 1, 0.28f));
        style.Disabled = Tinted(entry, Ui.Color(1, 1, 1, 0.05f));
        style.NormalForeground = Ui.SlateColor(Ui.Color(1, 1, 1, 1));
        style.HoveredForeground = Ui.SlateColor(Ui.Color(1, 1, 1, 1));
        style.PressedForeground = Ui.SlateColor(Ui.Color(1, 1, 1, 1));
        style.DisabledForeground = Ui.SlateColor(Ui.Color(1, 1, 1, 1));
        return style;
    }

    static FSlateBrush Tinted(FSlateBrush brush, FLinearColor colour)
    {
        var tinted = brush;
        tinted.TintColor = Ui.SlateColor(colour);
        return tinted;
    }

    /// <summary>Whether a name is being typed (the mod then sends the typing keys, not the drawer's).</summary>
    public bool IsRenaming() => renaming >= 0;

    /// <summary>Closes the drawer (the mod takes it off the screen).</summary>
    public void Close() => mod?.CloseDrawer();

    /// <summary>
    /// A key pressed while the drawer is open, from the mod's key tracker: up and down move through the loadouts, A or
    /// Enter equips, X or S saves, Y or R renames, View or Delete clears, and B, Esc or the loadouts key closes it. While a
    /// name is being typed, the typing keys (with Shift held or not).
    /// </summary>
    public void Key(FKey key, bool shift)
    {
        if (mod == null) return;
        string name = $"{key.KeyName}";
        if (renaming >= 0)
        {
            Type(name, shift);
            return;
        }
        if (name == "Up" || name == "Gamepad_DPad_Up") Move(-1);
        else if (name == "Down" || name == "Gamepad_DPad_Down") Move(1);
        else if (name == "Enter" || name == "Gamepad_FaceButton_Bottom")
        {
            Sounds.Click(this);
            DoAction(ActionEquip);
        }
        else if (name == "S" || name == "Gamepad_FaceButton_Left") DoAction(ActionSave);
        else if (name == "R" || name == "Gamepad_FaceButton_Top") DoAction(ActionRename);
        else if (name == "Delete" || name == "Gamepad_Special_Left") DoAction(ActionClear);
        else if (name == "Escape" || name == "Gamepad_FaceButton_Right" || name == "Gamepad_Special_Right" || mod.IsMenuKey(key)) Close();
    }

    /// <summary>The left stick: up and down move through the loadouts (<see cref="MenuStick"/>'s steps).</summary>
    public void Stick(int step)
    {
        if (mod == null || renaming >= 0) return;
        if (step == MenuStick.Up) Move(-1);
        else if (step == MenuStick.Down) Move(1);
    }

    /// <summary>Selects the loadout above or below (it stops at the ends, like the game's lists).</summary>
    void Move(int by)
    {
        if (mod == null) return;
        int to = selected + by;
        if (to < 0 || to >= mod.Count) return;
        selected = to;
        Sounds.Hover(this);
        lastFill = -100;
    }

    /// <summary>
    /// A key while a name is typed: a letter, digit, space or dash goes on it (replacing the old name, selected at first);
    /// Backspace takes one off (or clears the old name); Enter keeps it, Esc doesn't.
    /// </summary>
    void Type(string name, bool shift)
    {
        typedAt = World.RealTime(this);
        if (name == "Enter" || name == "Gamepad_FaceButton_Bottom")
        {
            FinishRenaming();
            return;
        }
        if (name == "Escape" || name == "Gamepad_FaceButton_Right")
        {
            mod?.ShowStatus("");
            StopRenaming();
            return;
        }
        if (name == "BackSpace")
        {
            if (replacing) typed = "";
            else if (UKismetStringLibrary.Len(typed) > 0) typed = UKismetStringLibrary.LeftChop(typed, 1);
            replacing = false;
            return;
        }
        string add = "";
        // The letters' keys are named by their capital.
        if (UKismetStringLibrary.Len(name) == 1) add = shift ? name : UKismetStringLibrary.ToLower(name);
        else if (name == "SpaceBar") add = " ";
        else if (name == "Hyphen") add = shift ? "_" : "-";
        else add = Digit(name);
        if (UKismetStringLibrary.Len(add) == 0) return;
        if (replacing)
        {
            typed = "";
            replacing = false;
        }
        if (UKismetStringLibrary.Len(typed) >= MaxName) return;
        // No spaces to start with.
        if (add == " " && UKismetStringLibrary.Len(typed) == 0) return;
        typed += add;
    }

    static string Digit(string name)
    {
        if (name == "Zero") return "0";
        if (name == "One") return "1";
        if (name == "Two") return "2";
        if (name == "Three") return "3";
        if (name == "Four") return "4";
        if (name == "Five") return "5";
        if (name == "Six") return "6";
        if (name == "Seven") return "7";
        if (name == "Eight") return "8";
        if (name == "Nine") return "9";
        return "";
    }

    /// <summary>Every frame while open: places and slides the drawer, marks the selected loadout, fills it in now and then.</summary>
    public void Refresh(double now)
    {
        if (mod == null) return;
        if (now >= 0 && openedAt < 0) openedAt = now;
        ApplyStyle();
        Place(now);
        // The game's ribbon can put its own words back (it's one of its tooltip's modules): keep ours.
        GamePanel.KeepTitle(ribbon, ribbonText, "Loadouts");
        int count = mod.Count;
        if (selected >= count) selected = count - 1;
        if (selected < 0) selected = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            bool shown = i < count;
            rows[i].SetVisibility(shown ? ESlateVisibility.Visible : ESlateVisibility.Collapsed);
            if (!shown) continue;
            // The selected one in the page's blue, as the game marks what's chosen.
            rows[i].SetBackgroundColor(i == selected ? Ui.Color(0.35f, 0.75f, 1, rows[i].IsHovered() ? 1 : 0.85f) : Ui.Color(1, 1, 1, 1));
        }
        if (renaming >= 0 && now >= 0)
        {
            // The old name selected; once typing, the name so far with a caret (steady while typing, then blinking).
            bool caret = !replacing && (int)((now - typedAt) * 2.5) % 2 == 0;
            renameText?.SetText(caret ? typed + "|" : typed);
            renameSelection?.SetBrushColor(replacing ? Ui.Color(0.2f, 0.45f, 0.85f, 0.75f) : Ui.Color(0, 0, 0, 0));
        }
        UpdateActionIcons();
        if (Debug() && !loggedTree && now >= 0 && now - openedAt > 0.5)
        {
            loggedTree = true;
            StyleDump.Write(drawer, "style: the loadouts drawer as built", 160);
        }
        if (now >= 0 && now - lastFill < 0.25) return;
        lastFill = now;
        Fill();
    }

    bool Debug() => mod != null && mod.Debug;

    void Fill()
    {
        if (mod == null) return;
        for (int i = 0; i < rows.Count && i < mod.Count; i++)
        {
            rowNames[i].SetText(mod.LoadoutName(i));
            string state = mod.HasLoadout(i) ? mod.StateText(i) : "Empty";
            rowStates[i].SetText(UKismetStringLibrary.ToUpper(state));
            var colour = !mod.HasLoadout(i) ? Ui.Color(0.4f, 0.42f, 0.46f, 1)
                : state == "EQUIPPED" ? Ui.Color(0.08f, 0.65f, 0.98f, 1) : Ui.Color(0.84f, 0.87f, 0.95f, 1);
            rowStates[i].SetColorAndOpacity(Ui.SlateColor(colour));
        }
        if (iconsShownFor != selected || iconsRevision != mod.Revision)
        {
            iconsShownFor = selected;
            iconsRevision = mod.Revision;
            ShowGear(mod.LoadoutItems(selected));
        }
        status?.SetText(mod.Status);
    }

    /// <summary>The selected loadout's gear, in the game's frames, and its enchantments as the item panel lists them.</summary>
    void ShowGear(List<SavedItem> items)
    {
        if (mod == null) return;
        for (int k = 0; k < icons.Count; k++)
        {
            if (k >= items.Count)
            {
                iconSlots[k].SetVisibility(ESlateVisibility.Collapsed);
                continue;
            }
            iconSlots[k].SetVisibility(ESlateVisibility.HitTestInvisible);
            if (mod.RarityBrush(items[k].Rarity, out FSlateBrush background))
            {
                iconFrames[k].SetBrush(background);
                iconFrames[k].SetColorAndOpacity(Ui.Color(1, 1, 1, 1));
            }
            else
            {
                iconFrames[k].SetBrush(new FSlateBrush());
                iconFrames[k].SetColorAndOpacity(Items.RarityColour(items[k].Rarity));
            }
            bool marked = !Items.IsNone(items[k].Enchant) && mod.EnchantMark(out FSlateBrush mark);
            if (marked)
            {
                mod.EnchantMark(out FSlateBrush markBrush);
                markBrush.ImageSize = new FDeprecateSlateVector2D { X = (float)(iconSizeShown * 0.28), Y = (float)(iconSizeShown * 0.32) };
                iconMarks[k].SetBrush(markBrush);
            }
            iconMarks[k].SetVisibility(marked ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
            icons[k].SetBrushFromSoftTexture(UInventoryHelperLibrary.GetTypeIcon(this, GameUI.Tag(items[k].Type), 0), false);
        }
        if (enchantList == null) return;
        enchantList.ClearChildren();
        int shown = 0;
        foreach (var item in items)
        {
            if (Items.IsNone(item.Enchant)) continue;
            shown++;
            enchantList.AddChild(EnchantLine(item));
        }
        if (shown == 0)
        {
            var none = mod.HasLoadout(selected)
                ? Ui.Text(WidgetTree, "Not enchanted", 29)
                : Ui.Text(WidgetTree, "Empty. Save puts the gear you have on into this loadout.", 36);
            if (none != null)
            {
                none.SetFont(mod.GameFont(mod.HasLoadout(selected) ? GameLook.TagFace : GameLook.TextFace, mod.HasLoadout(selected) ? 29 : 36));
                none.SetColorAndOpacity(Ui.SlateColor(Ui.Color(0.57f, 0.59f, 0.65f, 1)));
                none.SetAutoWrapText(true);
                enchantList.AddChild(none);
            }
        }
    }

    /// <summary>An enchantment, as the item panel shows one: its name over the item it's on, in the purple box.</summary>
    UWidget? EnchantLine(SavedItem item)
    {
        if (mod == null) return null;
        var column = Ui.Row(WidgetTree);
        var title = Ui.Text(WidgetTree, $"{item.EnchantName} {Tier(item.EnchantSpecific)}", 40);
        var on = Ui.Text(WidgetTree, item.Name, 32);
        if (column == null || title == null || on == null) return null;
        title.SetFont(mod.GameFont(GameLook.TextFace, 40));
        title.SetColorAndOpacity(Ui.SlateColor(Ui.Color(0.8f, 0.82f, 0.91f, 1)));
        title.SetShadowOffset(Vec2(5, 5));
        title.SetShadowColorAndOpacity(Ui.Color(0, 0, 0, 0.5f));
        on.SetFont(mod.GameFont(GameLook.TextFace, 32));
        on.SetColorAndOpacity(Ui.SlateColor(Ui.Color(0.57f, 0.59f, 0.64f, 1)));
        var titleSlot = column.AddChild(title) as UHorizontalBoxSlot;
        titleSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        var onSlot = column.AddChild(on) as UHorizontalBoxSlot;
        onSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        onSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Right);
        onSlot?.SetSize(new FSlateChildSize { Value = 1, SizeRule = ESlateSizeRule.Fill });
        onSlot?.SetPadding(new FMargin { Left = 24 });
        var box = Ui.Panel(WidgetTree, column, Ui.Color(1, 1, 1, 1), 0);
        if (box == null) return column;
        box.SetPadding(new FMargin { Left = 28, Right = 28, Top = 8, Bottom = 8 });
        if (mod.EntryBrush(out FSlateBrush entry)) box.SetBrush(Tinted(entry, Ui.Color(1, 1, 1, 0.1f)));
        box.SetBrushColor(Ui.Color(0.56f, 0.1f, 0.91f, 1));
        var holder = Ui.Column(WidgetTree);
        if (holder == null) return box;
        var slot = holder.AddChild(box) as UVerticalBoxSlot;
        slot?.SetPadding(new FMargin { Bottom = 8 });
        return holder;
    }

    /// <summary>"III" from SW.Enchantment.Swirling.III (an enchantment's tier), else nothing.</summary>
    static string Tier(string specific)
    {
        string last = Items.LastPart(specific);
        return last == "I" || last == "II" || last == "III" || last == "IV" || last == "V" ? last : "";
    }

    /// <summary>
    /// Over the inventory page's item panel (the mod measures it when the drawer opens), at the page's scale, sliding in
    /// from the right.
    /// </summary>
    void Place(double now)
    {
        if (mod == null || drawer == null || scaler == null || sizer == null) return;
        var slot = drawer.Slot as UCanvasPanelSlot;
        var mine = GetCachedGeometry();
        var size = USlateBlueprintLibrary.GetLocalSize(mine);
        if (slot == null || !(size.X > 10) || !(size.Y > 10)) return;
        var from = Vec2(size.X * 0.67, size.Y * 0.2);
        var to = Vec2(size.X * 0.97, size.Y * 0.9);
        if (mod.DrawerRect(out FVector2D topLeft, out FVector2D bottomRight))
        {
            var a = USlateBlueprintLibrary.AbsoluteToLocal(mine, topLeft);
            var b = USlateBlueprintLibrary.AbsoluteToLocal(mine, bottomRight);
            if (b.X - a.X > 100 && b.Y - a.Y > 200 && a.X > -1 && b.X < size.X + 1)
            {
                from = a;
                to = b;
            }
        }
        // The page's scale: its design units to this screen's (the page sits in scale boxes this screen doesn't have).
        var abs0 = USlateBlueprintLibrary.LocalToAbsolute(mine, Vec2(0, 0));
        var abs1 = USlateBlueprintLibrary.LocalToAbsolute(mine, Vec2(100, 0));
        double myScale = (abs1.X - abs0.X) / 100;
        double scale = myScale > 0 ? mod.PageScale() / myScale : 0.5;
        if (!(scale > 0.05) || !(scale < 5)) scale = 0.5;
        scaler.SetUserSpecifiedScale((float)scale);
        sizer.SetWidthOverride((float)((to.X - from.X) / scale));
        FitIcons((to.X - from.X) / scale);
        sizer.SetHeightOverride((float)((to.Y - from.Y) / scale));
        slot.SetPosition(from);
        double t = openedAt < 0 || now < 0 ? 0 : (now - openedAt) / SlideSeconds;
        if (t > 1) t = 1;
        double ease = 1 - (1 - t) * (1 - t) * (1 - t);
        drawer.SetRenderTranslation(Vec2((1 - ease) * (to.X - from.X) * 0.35, 0));
        drawer.SetRenderOpacity((float)ease);
    }

    /// <summary>The hints' icons: the game's icon for each action's button on the controller or keyboard in use.</summary>
    void UpdateActionIcons()
    {
        if (mod == null) return;
        bool gamepad = mod.Gamepad();
        // While a name is typed, only Done (Enter or A).
        if (actionTexts.Count > ActionEquip) actionTexts[ActionEquip].SetText(renaming >= 0 ? "Done" : "Equip");
        for (int k = 0; k < actionButtons.Count; k++)
            actionButtons[k].SetVisibility(renaming < 0 || k == ActionEquip ? ESlateVisibility.Visible : ESlateVisibility.Collapsed);
        for (int k = 0; k < actionIcons.Count; k++)
        {
            string key = ActionKey(k, gamepad);
            bool found = mod.IconRow(key, gamepad, out FDataTableRowHandle row);
            string rowName = found ? $"{row.RowName}" : "";
            if (found && rowName != shownRows[k])
            {
                shownRows[k] = rowName;
                actionIcons[k].SetInputAction(row);
            }
            bool hasIcon = found && actionIcons[k].GetIcon().ResourceObject != null;
            actionIcons[k].SetVisibility(hasIcon ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
            actionKeyCaps[k].SetVisibility(hasIcon ? ESlateVisibility.Collapsed : ESlateVisibility.HitTestInvisible);
            actionKeyNames[k].SetText(KeyLabel(key));
        }
    }

    static string ActionKey(int action, bool gamepad)
    {
        if (action == ActionEquip) return gamepad ? "Gamepad_FaceButton_Bottom" : "Enter";
        if (action == ActionSave) return gamepad ? "Gamepad_FaceButton_Left" : "S";
        if (action == ActionRename) return gamepad ? "Gamepad_FaceButton_Top" : "R";
        if (action == ActionClear) return gamepad ? "Gamepad_Special_Left" : "Delete";
        return gamepad ? "Gamepad_FaceButton_Right" : "Escape";
    }

    static string KeyLabel(string key)
    {
        if (key == "Gamepad_FaceButton_Bottom") return "A";
        if (key == "Gamepad_FaceButton_Left") return "X";
        if (key == "Gamepad_FaceButton_Top") return "Y";
        if (key == "Gamepad_FaceButton_Right") return "B";
        if (key == "Gamepad_Special_Left") return "View";
        if (key == "Escape") return "Esc";
        if (key == "Delete") return "Del";
        return key;
    }

    // ---------------------------------------------------------------- Actions

    void DoAction(int action)
    {
        if (mod == null) return;
        double now = World.RealTime(this);
        string name = mod.LoadoutName(selected);
        if (action == ActionEquip) mod.EquipLoadout(selected);
        else if (action == ActionSave)
        {
            // Saving over a loadout asks again first.
            if (mod.HasLoadout(selected) && !Confirmed(action, now))
            {
                mod.ShowStatus($"Save again to replace {name} with the gear you have on.");
                Sounds.Hover(this);
                lastFill = -100;
                return;
            }
            mod.SaveLoadout(selected);
        }
        else if (action == ActionRename) StartRenaming();
        else if (action == ActionClear)
        {
            if (!mod.HasLoadout(selected)) return;
            if (!Confirmed(action, now))
            {
                mod.ShowStatus($"Clear again to empty {name}.");
                Sounds.Hover(this);
                lastFill = -100;
                return;
            }
            mod.ClearLoadout(selected);
        }
        else Close();
        lastFill = -100;
    }

    /// <summary>True when this action was just asked for on the same loadout (the second press); else remembers this one.</summary>
    bool Confirmed(int action, double now)
    {
        bool again = confirmAction == action && confirmIndex == selected && now - confirmAt < 4;
        confirmAction = again ? -1 : action;
        confirmIndex = selected;
        confirmAt = now;
        return again;
    }

    /// <summary>Starts typing a new name for the selected loadout, from its name now (Backspace takes it off).</summary>
    void StartRenaming()
    {
        if (mod == null || renameBox == null) return;
        renaming = selected;
        typed = mod.LoadoutName(selected);
        replacing = true;
        typedAt = World.RealTime(this);
        renameBox.SetVisibility(ESlateVisibility.HitTestInvisible);
        mod.ShowStatus(mod.Gamepad() ? "Type a new name on the keyboard. A keeps it, B keeps the old one." : "Type a new name, then Enter. Esc keeps the old one.");
        Sounds.Click(this);
        lastFill = -100;
    }

    void FinishRenaming()
    {
        if (renaming < 0 || mod == null) return;
        int index = renaming;
        StopRenaming();
        mod.RenameLoadout(index, typed);
    }

    void StopRenaming()
    {
        renaming = -1;
        renameBox?.SetVisibility(ESlateVisibility.Collapsed);
        lastFill = -100;
    }

    void Row0() => OnRow(0);
    void Row1() => OnRow(1);
    void Row2() => OnRow(2);
    void Row3() => OnRow(3);
    void Row4() => OnRow(4);
    void Row5() => OnRow(5);

    /// <summary>A row clicked: the first click selects it, a second equips it.</summary>
    void OnRow(int index)
    {
        if (mod == null || renaming >= 0) return;
        Sounds.Click(this);
        if (index != selected)
        {
            selected = index;
            lastFill = -100;
            return;
        }
        DoAction(ActionEquip);
    }

    void ClickEquip()
    {
        if (renaming >= 0) FinishRenaming();
        else DoAction(ActionEquip);
    }

    void ClickSave() => DoAction(ActionSave);
    void ClickRename() => DoAction(ActionRename);
    void ClickClear() => DoAction(ActionClear);
    void OnClickAway() => Close();
    void HoverSound() => Sounds.Hover(this);

    static FVector2D Vec2(double x, double y) => new FVector2D { X = x, Y = y };
}
