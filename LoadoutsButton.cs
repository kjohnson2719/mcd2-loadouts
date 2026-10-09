using NeoRune;
using UE.CommonUI;
using UE.CoreUObject;
using UE.Engine;
using UE.SlateCore;
using UE.UMG;

namespace Loadouts;

/// <summary>
/// The inventory page's "Loadouts" button, beside Effects &amp; Stats and made like it: that button's own style (its
/// background, hover and press), its label's font, and the game's icon for the key that opens the loadouts. Laid out in
/// the page's units, at its scale; the label gets smaller if the space is narrow.
/// </summary>
public class LoadoutsButton : ModWidget
{
    const float IconSize = 80;
    const float LabelPadding = 20;

    ModActor? mod;
    UScaleBox? scaler;
    USizeBox? sizer;
    UButton? button;
    UTextBlock? label;
    UCommonActionWidget? icon;
    USizeBox? iconBox;
    string shownRow;
    bool styled;
    double fittedFor;

    protected override UWidget? Build(UWidgetTree tree)
    {
        shownRow = "";
        fittedFor = -1;
        scaler = UGameplayStatics.SpawnObject(Unreal.ClassOf<UScaleBox>(), tree) as UScaleBox;
        sizer = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        button = UGameplayStatics.SpawnObject(Unreal.ClassOf<UButton>(), tree) as UButton;
        var row = Ui.Row(tree);
        label = Ui.Text(tree, "Loadouts", 44);
        icon = UGameplayStatics.SpawnObject(Unreal.ClassOf<UCommonActionWidget>(), tree) as UCommonActionWidget;
        iconBox = UGameplayStatics.SpawnObject(Unreal.ClassOf<USizeBox>(), tree) as USizeBox;
        if (scaler == null || sizer == null || button == null || row == null || label == null || icon == null || iconBox == null) return null;
        scaler.SetStretch(UE.Slate.EStretch.UserSpecified);
        var labelSlot = row.AddChild(label) as UHorizontalBoxSlot;
        labelSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        labelSlot?.SetPadding(new FMargin { Left = LabelPadding, Right = LabelPadding });
        iconBox.SetWidthOverride(IconSize);
        iconBox.SetHeightOverride(IconSize);
        iconBox.AddChild(icon);
        var iconSlot = row.AddChild(iconBox) as UHorizontalBoxSlot;
        iconSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        button.AddChild(row);
        var rowSlot = row.Slot as UButtonSlot;
        rowSlot?.SetHorizontalAlignment(EHorizontalAlignment.HAlign_Center);
        rowSlot?.SetVerticalAlignment(EVerticalAlignment.VAlign_Center);
        // The controller opens it with its key, not by moving onto it.
        button.IsFocusable = false;
        button.OnClicked += OnClick;
        button.OnHovered += OnHover;
        sizer.AddChild(button);
        scaler.AddChild(sizer);
        return scaler;
    }

    public void Begin(ModActor owner) => mod = owner;

    /// <summary>
    /// Matches Effects &amp; Stats (its style and label font, once), takes the space given (in the page's units, at its
    /// scale), and shows the icon of the key's row (or none when the game has no icon for the key).
    /// </summary>
    public void Refresh(UCommonButtonBase stats, double width, double height, double scale, FDataTableRowHandle row, bool hasRow)
    {
        if (button == null || label == null || icon == null || iconBox == null || scaler == null || sizer == null) return;
        if (!styled)
        {
            styled = true;
            button.SetStyle(stats.NormalStyle);
            var ore = stats as UE.OreUI.UOreUIButtonBase;
            if (ore != null && ore.TextBlockWidget != null)
            {
                label.SetFont(ore.TextBlockWidget.Font);
                label.SetColorAndOpacity(ore.TextBlockWidget.ColorAndOpacity);
                label.SetShadowOffset(ore.TextBlockWidget.ShadowOffset);
                label.SetShadowColorAndOpacity(ore.TextBlockWidget.ShadowColorAndOpacity);
            }
        }
        scaler.SetUserSpecifiedScale((float)scale);
        sizer.SetWidthOverride((float)width);
        sizer.SetHeightOverride((float)height);
        string rowName = hasRow ? $"{row.RowName}" : "";
        if (hasRow && rowName != shownRow) icon.SetInputAction(row);
        shownRow = rowName;
        bool hasIcon = hasRow && icon.GetIcon().ResourceObject != null;
        iconBox.SetVisibility(hasIcon ? ESlateVisibility.HitTestInvisible : ESlateVisibility.Collapsed);
        Fit(width, hasIcon);
    }

    /// <summary>The label as large as the button's own (Effects &amp; Stats') while it fits; smaller when it doesn't.</summary>
    void Fit(double width, bool hasIcon)
    {
        if (label == null) return;
        double key = width + (hasIcon ? 0.5 : 0);
        if (key == fittedFor) return;
        fittedFor = key;
        double room = width - LabelPadding * 2 - (hasIcon ? IconSize : 0) - 40;
        var font = label.Font;
        for (int size = 44; size >= 24; size -= 2)
        {
            font.Size = size;
            label.SetFont(font);
            label.ForceLayoutPrepass();
            if (label.GetDesiredSize().X <= room) break;
        }
    }

    void OnClick() => mod?.OnLoadoutsButton();

    void OnHover() => Sounds.Hover(this);
}
