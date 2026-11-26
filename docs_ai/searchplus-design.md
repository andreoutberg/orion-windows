# Search+ design language

Give the page a clean edge and the browser controls a clear place. Search+
uses a native Windows window, a single navigation surface, and a compact rail.
The active workspace and tab stay identifiable as the rail folds.

## Toolbar and rail

- Give the address field the most horizontal room. Group Back, Forward and
  Reload at its leading edge. Use neutral, borderless icons for browser actions.
  Keep secondary commands in More and keep the Windows caption buttons and an
  empty drag region available as the window narrows. `Ctrl+L` edits the address.
  `Ctrl+K` opens local tab and workspace search.
- Anchor the workspace switcher above the tabs. At 224 DIPs, show the workspace
  icon, name and disclosure chevron. At 48 DIPs, center the icon on the same
  24-DIP axis as the favicons and New Tab button. Preserve tab order and the
  selected tab. Expose the current workspace name through the folded control
  and identify the active item when its switcher opens.
- Use “workspace” consistently in the switcher, its creation and naming
  actions, and its pin menu. Orion's saved tab groups back this concept.
  Keep the existing pin scopes explicit in the menu.

These choices adapt Apple's guidance on familiar sidebar symbols and grouping
frequent toolbar actions to the Windows title bar. [Apple Sidebars](https://developer.apple.com/design/human-interface-guidelines/sidebars) · [Apple Toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars) · [Windows title bars](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design)

## Type and spacing

Use the WinUI text font, Segoe UI Variable where available. At the default
text size, use these roles and dimensions in device-independent pixels.

| Element | Type and geometry |
| --- | --- |
| Workspace name | 13 semibold, centered vertically in a control at least 36 high. Allow a long name to ellipsize and expose the complete name on the control. |
| Expanded tab | 13 regular beside a 16-DIP favicon, with 8 DIPs between the icon well and title. Center both on the same line. Let the row grow with text size in both flat and nested layouts. |
| Search result | Body text for the title, 12-DIP secondary caption for workspace and site, with a 2-DIP gap. Keep enough context visible to distinguish duplicate titles. |
| Toolbar | 36-DIP button targets and a 36-DIP address field at default text size. Use 4 DIPs between related controls, 8-DIP button corners and a 10-DIP field radius. Let the field enclosure follow its text box when text grows. |

Use the same icon slot for a site's favicon or its fallback glyph, including
sites with transparent artwork. Give each folded list item the tab title as
its accessible name. [Apple Typography](https://developer.apple.com/design/human-interface-guidelines/typography) · [Windows typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)

## Selection, focus and materials

Use a short violet mark at the leading edge of the active tab. The mark stays
in place when the cursor moves to another row. It gives the graphite or warm
off-white chrome an accent that also identifies the page being shown.

| State | Appearance |
| --- | --- |
| Rest | A clear row with a legible title and one site icon. |
| Hover or press | WinUI's transient neutral row fill. A selected row retains its leading mark throughout. |
| Selected | A persistent fill and rounded leading mark. Use the native tree indicator (3 × 16 DIPs) and the list indicator in the folded rail. |
| Keyboard focus | A system focus outline around the focused control, independent of the current tab. Both cues remain visible when focus moves. |
| Windows contrast theme | Follow the control's system color pair. The expanded tree uses Highlight on Window at rest and ButtonText on ButtonFace for hover or press. The folded list uses HighlightText on Highlight for its selected row. |

Use one Mica backdrop behind the transparent toolbar and rail. Let Windows
supply its opaque fallback. A 1-DIP rail divider defines the native page
edge. Keep the address field and workspace selector on shallow tinted fills.
Palette and button resources belong in `Orion/QuietChrome.xaml`.
[Apple Color](https://developer.apple.com/design/human-interface-guidelines/color) · [Apple Materials](https://developer.apple.com/design/human-interface-guidelines/materials) · [Windows Mica](https://learn.microsoft.com/en-us/windows/apps/design/style/mica) · [Windows contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)

## Motion

Commit the layout width once so the renderer's child HWND and its pointer
coordinates share one edge. Fade the arriving rail contents for 120 ms when
folding and 160 ms when expanding. The workspace anchor stays still. Search
results fade in for 140 ms on opening and respond immediately to each typed
character. Repeated commands take effect during the fade. Apply the final
state immediately when Windows animations are disabled. [Apple Motion](https://developer.apple.com/design/human-interface-guidelines/motion)

## Acceptance

- At a narrow width with a named profile and an active download, check address
  editing, tab search, the drag region, and Windows caption buttons.
- Test large Windows text separately from 100%, 125% and 200% display scaling.
  Inspect the workspace switcher, both tab templates, the address focus outline
  and two search results with identical titles and different sites.
- In both rail widths, select one tab, hover another, then move keyboard focus.
  Identify all three in light, dark and a Windows contrast theme. In the folded
  rail, verify that a screen reader announces the tab title and selection.
- Fold and expand twice, including during the fade and with animations disabled.
  Check clicks at the native page edge. Switch to another workspace and back,
  confirming its last tab is selected and the focus indicator remains visible
  for keyboard navigation.

## Pin behavior

- **Pin to this workspace** stores the tab in the saved workspace. Name and
  save **This Window** to make workspace-only pins available there. **Pin
  across workspaces** keeps one profile-scoped pin whose tab appears in every
  non-private workspace. Existing pin rows keep their original scope.
- Navigation changes the live page. **Reset to saved page** returns to the
  saved URL. **Use current page as saved page** updates the pin, including its
  projections in other workspaces. A restored pin opens its saved page.
- Verify pinning, changing scope in both directions, unpinning, and changing
  the saved page with two workspaces and a second window open. Restart and
  check each scope and saved page again.
- Right-click a workspace-only pin and choose **Pin folder** to create a
  folder or move the pin into one. Folder headers expand and collapse in the
  sidebar. The folded rail exposes the same members under **Pin folders**.
  The folder menu can rename, reorder, or unpack it. Unpacking returns its
  pins to the workspace tab list. Check membership, order, and expansion
  after restarting.
