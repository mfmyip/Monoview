# Monoview: app icon design brief

## The product

Monoview is a fast, calm PDF and image viewer and editor for Windows 11. It does the everyday jobs that macOS Preview
does: read, search, reorder and merge pages, mark up, sign and fill forms. It has no account, no ads and no ribbon.
It is open source under the MIT license.

The brand is the calm. The icon must look quiet and confident next to loud PDF tools, not compete with them.

## The idea

**One clear lens over a page and a picture.** "Mono" means one: one lens, one clean view. The lens shows what the app
opens: a document on one side, a photo on the other.

## Motifs

| Motif | What it means | How to use it |
|---|---|---|
| **Monocle** | A single reading glass; the name | The main shape. A round lens with a thick rim and a short chain or cord that hangs from the rim and curves away. **No handle.** |
| **Glass** | Clear, light, nothing in the way | A soft highlight across the upper part of the lens; a thin bright inner edge on the rim |
| **Page** | Documents, the main use | Seen through the lens: a white page with three or four soft text lines |
| **Picture** | Images, which Monoview also opens | Seen through the lens, next to the page: a small landscape with a hill and a sun |
| **Leaf** | One sheet of a book | Optional: the page can have one folded corner |

## Style

- Windows 11 Fluent style: a rounded square tile, soft gradients, a light drop shadow, no hard outlines.
- Light tile, white to pale blue. The rim is deep navy, about `#2C5C94` to `#0B2545`. A faint pink glow in one corner is
  fine.
- Calm and premium, not cute. No face, no mascot, no text or letters in the icon.
- It must read at 16 x 16 px. At that size, show only the lens ring, the chain and a split of light and dark inside.

## Do not

- **Do not draw a magnifying glass with a handle.** Apple's Preview icon is a magnifier over photos. We must not look
  like it.
- Do not use red as a main color. Adobe Acrobat owns red PDF icons.
- Do not use a swirl, a wave or four panes in a square. Those belong to Microsoft Edge and the Windows logo.
- Do not use stock icons, icon packs or AI images trained on other brands. The icon must be original, and we must hold
  the rights to it under MIT.

## Starting point

The current icon is `src/Monoview.App/Assets/Monoview.svg`. It already has the tile, the navy rim, the glass highlight
and the page plus picture inside the lens. It still uses a **magnifier handle**. Replace the handle with the monocle
chain, and keep the rest of the style.

## Deliverables

1. A master SVG, 256 x 256, with the shapes kept as editable vectors.
2. Hand-tuned versions for small sizes, 16, 20, 24 and 32 px. Simplify, do not only scale down.
3. A multi-size `Monoview.ico` with 16, 20, 24, 32, 40, 48, 64 and 256 px.
4. MSIX and Store assets, for later: `Square44x44Logo`, `Square150x150Logo`, `Wide310x150Logo`, `StoreLogo` and a
   300 x 300 Store logo, each at scales 100, 150 and 200.
5. Checks on the light and dark taskbar, in the Start menu and in File Explorer next to other PDF apps.

## How we judge it

- Can someone see "a single lens over a page" in under a second at 48 px?
- At 16 px, is it still clearly ours, and different from Acrobat, Edge and Preview?
- Does it look calm next to the other icons on a Windows 11 taskbar?
