# Codex Usage icon

- Generated with the built-in image generation tool.
- Design: an azure-to-blue rounded tile with three bold white usage bars; no letter C or text.
- Master: `codex_widget_provider/DesignAssets/CodexUsageIcon.png` (1254 × 1254 RGBA PNG, transparent corners).
- The master is kept outside the package asset directories; `gen_assets.ps1` exports the required PNG sizes with high-quality bicubic interpolation.
- Exports:
  - `codex_widget_provider/ProviderAssets/Codex_Icon.png`: 48 × 48.
  - `codex_widget_provider/Assets/Square44x44Logo.png`: 44 × 44.
  - `codex_widget_provider/Assets/StoreLogo.png`: 50 × 50.
  - `codex_widget_provider/Assets/Square150x150Logo.png`: 150 × 150.
- The separately maintained widget picker preview is exported at 300 × 304 by the same script.
- Rebuild and install the updated package to use the new installed icons.

## Generation prompt

```text
Use case: logo-brand / precise-object-edit.
Asset type: production Windows app and widget icon for Codex Usage.
Input image is the EDIT TARGET: current cyan rounded-square icon with the letter C. Replace the letter-based identity completely with a crisp abstract usage-meter symbol. This is one final icon asset, not an icon sheet, product mockup, or presentation.

Design:
A bold rounded-square app tile filling the square image bounds, with a modern saturated electric-blue background, subtly transitioning from azure blue at upper left to royal blue at lower right (#168DFF to #0866EC). Corner radius about 22 percent of width. No exterior shadow, no perspective, no bevel, no glossy reflections.
Inside, a large white usage-meter symbol: exactly three solid vertical bars with gently rounded ends, increasing in height from left to right. All bars have the same thick width, share the same baseline, and have generous gaps. The composition is optical-center aligned, with a 21 percent safe margin. The heights are approximately 25, 40, and 56 percent of the canvas. Pure white bars, very simple, strongly recognizable at 24 and 48 pixels. The symbol should read as metered consumption / usage statistics. No surrounding ring, no graph axes, no border box, no extra icons.
Use smooth clean geometric contours and an understated polished Windows Fluent aesthetic, matching the blue theme of a lightweight usage dashboard.

Hard requirements:
NO letter C. No letters, words, numbers, logo text, percent sign, tick marks, fine lines, arrows, busy details or labels anywhere.
Square 1:1 canvas, high resolution preferably 1024 x 1024. Tile touches the four canvas edges at the middle of each edge. Background ONLY outside the rounded tile must be genuinely transparent alpha. Inside the tile must be completely opaque, clean, uniformly smooth except the subtle blue color transition. No white rectangle behind the tile, no printed checkerboard, no dark holes, no black patches, no texture, no watermark.
Deliver one finished icon.
```
