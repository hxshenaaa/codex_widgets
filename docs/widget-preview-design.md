# Widget picker preview

- Asset: `codex_widget_provider/ProviderAssets/Codex_Screenshot.png`
- Source artwork: `codex_widget_provider/DesignAssets/CodexUsagePreview.png`.
- Export: **300 × 304 RGBA PNG**, using proportional containment (no cropping or stretching).
- Requirement: [Microsoft widget picker integration](https://learn.microsoft.com/en-us/windows/apps/design/widgets/widgets-picker-integration#screenshot-image-requirements).
- The earlier 288 × 360 portrait asset overflowed the picker's preview area; the design is now reflowed into a near-square layout with the full footer visible.
- Generated with the built-in image generation tool; `gen_assets.ps1` performs the final size export, preserving generated alpha and using transparent padding where necessary.
- Example values (73%, 16.8M, reset/update times) are illustrative, not live account data.
- Five-hour content remains removed.
- `build_msix.ps1` regenerates assets from the maintained masters before packaging, so an arbitrary-sized source cannot become the packaged preview.
- The live widget's content/layout is separate from this picker preview.

## Generation prompt

```text
Use case: ui-mockup / precise layout edit for a Windows widget picker asset.
EDIT TARGET: the supplied current Codex Usage portrait preview.
Critical fix: the current portrait image is clipped by the Windows widget picker. Reflow the SAME design into a NEAR-SQUARE canvas, width:height exactly 75:76, intended export 300 x 304 pixels (you may render at 1200 x 1216). Do not keep the old 4:5 portrait layout. Do not merely crop the bottom or squash the typography.

Preserve the electric-blue primary accent, pale icy blue card background, dark navy typography, thin light blue perimeter border, rounded data panels, and all example data. Slightly compact the vertical spacing so the complete footer fits comfortably. Card rounded rectangle fills canvas with corner radius about 16px at final size, no exterior padding or shadow. Interior opaque and visually clean; genuinely transparent alpha outside rounded corners only. White/blue light theme, no dark patches or texture.

Target final-pixel layout (scale proportionately for high-res output):
Header at x20 y16: bold "Codex Usage", small pale circular ellipsis at upper right.
Centered blue "73" and dark "%" form one natural baseline "73%" around y48-108, hero height approximately 58px.
Centered label "Weekly remaining" around y116.
Centered reset line "09月25日 10:13 重置" around y140.
Rounded blue progress bar (73% filled, pale blue-gray track), x18 to282, y162 to173.
Exactly two rounded pale-blue panels, y186 to258, with a 10px gap:
Left panel label "累计 tokens", bold number "16.8M".
Right panel label "近 7 天", smooth vivid-blue sparkline with a subtle pale-blue area fill below.
Footer entirely visible at y273-291, with comfortable bottom padding: small "更新 14:42" at left; small pale-blue rounded refresh pill at right with refresh arrow and "刷新".
All text must stay inside the 300x304 canvas and be crisp at that size. No element may cross the bottom boundary.

Exact text: "Codex Usage", "73%", "Weekly remaining", "09月25日 10:13 重置", "累计 tokens", "16.8M", "近 7 天", "更新 14:42", "刷新".
No 5h content, no C logo, no duplicate Weekly block, no new metrics or text. No watermark, mockup context, black smears, printed checkerboard, perspective, decorative shadows, or variant sheets. One final polished icon-picker preview, near square, all content visible.
```
