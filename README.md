# Loadouts

A Minecraft Dungeons II mod: save the gear you have on as a loadout, and swap between loadouts in one press, right on the
inventory page. Enchantments move with your gear.

- **On the inventory page.** A **Loadouts** button sits beside **Effects & Stats**. It opens your loadouts over the
  item panel, built from the game's own panels, fonts and item frames.
- **Save** keeps the gear you have on (weapons, armor, artifacts, talismans) and each item's enchantment.
- **Equip** puts a loadout's gear back on, in the slots it was saved in. Only what's different is touched: gear already
  on stays on, and enchantments already right aren't redone.
- **Enchantments move with your gear** (a setting, on by default). Gear coming off is disenchanted, which gives its
  enchantment points back; gear going on is enchanted again and upgraded to the tier it had. Re-enchanting costs what it
  costs at the Enchantsmith, about one Spring Stone per tier (Spring Stones aren't refunded).
- Each loadout shows what switching to it would change, its gear in the game's rarity frames, and its enchantments.
- Rename loadouts, clear them, and save over them (it asks twice).
- Loadouts are saved per character.
- Swapping works in single player and in games you host. When you've joined someone else's game, the host's game decides
  what you can equip and enchant, so the loadouts say so instead (saving and renaming still work).

## Controls

| | Controller | Keyboard and mouse |
| --- | --- | --- |
| Open the loadouts (inventory page) | Right stick click | L, or click Loadouts |
| Move between loadouts | Stick or D-pad | Arrows, or click one |
| Equip | A | Enter, or click it again |
| Save the gear you have on (twice to replace) | X | S |
| Rename | Y | R |
| Clear (twice) | View | Delete |
| Close | B (or right stick click) | Esc (or L) |

A "next loadout" key (unbound by default) switches loadouts anywhere. The keys, the number of loadouts (2 to 6) and
moving enchantments are in the game's Settings, Mods tab.

## Install

1. Install [Blueprint Loader](https://www.nexusmods.com/minecraftdungeons2/mods/2) (2.0 or newer, for the settings page).
2. Put the `Loadouts` folder into the game's `Dungeons\Content\Paks\~mods` folder.

## Developing

Built with [NeoRune](https://www.nexusmods.com/minecraftdungeons2/mods/47) (C#, .NET 10). `dotnet build` compiles the
mod and installs it into the game (close the game first); development builds write diagnostics, which
`neorune log Loadouts` shows. The mod's look is taken from the game's own widgets at runtime (`StyleDump` logs how
the game draws its panels).

The Nexus art is in `nexus/`: `render-art.ps1` and `screenshots/render-shots.ps1` render it with Microsoft Edge.

## Releasing

Push a version tag and GitHub Actions builds the release zip:

```
git tag v1.0
git push origin v1.0
```

## License

MIT
