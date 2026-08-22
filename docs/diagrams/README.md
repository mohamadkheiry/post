# دیاگرام‌های معماری

هر دیاگرام دو artefact هم‌نام دارد:

- `.mmd`: source قابل review و تغییر؛
- `.svg`: render قابل نمایش در Git و مستندات.

SVGهای این revision با `@mermaid-js/mermaid-cli 11.16.0` رندر شده‌اند.

| نام | نما |
| --- | --- |
| `system-context` | بازیگران و سیستم‌های بیرونی |
| `container` | containerها و dependencyهای اصلی |
| `deployment` | topology استقرار Edge/Core |
| `use-cases` | actorها و use caseهای Phase 1 |
| `activity-inbound` | جریان ثبت inbound |
| `activity-close-bag` | جریان بستن کیسه |
| `activity-close-dispatch` | جریان بستن دیسپچ |
| `sequence-device-edge-core` | توالی Device → Edge → Postal/Core |
| `data-model` | موجودیت‌ها و رابطه‌های مهم |

## بازتولید SVG

با Node.js و Mermaid CLI:

```powershell
$env:PUPPETEER_EXECUTABLE_PATH = 'path-to-a-local-chromium-executable'
Get-ChildItem .\docs\diagrams -Filter *.mmd | ForEach-Object {
  $svg = [IO.Path]::ChangeExtension($_.FullName, '.svg')
  npx --yes @mermaid-js/mermaid-cli@11.16.0 -i $_.FullName -o $svg -b transparent
}
.\scripts\verify-diagrams.ps1
```

render باید بعد از آخرین تغییر source ساخته شود. CI علاوه بر وجود pair، جدیدتر یا هم‌زمان‌بودن timestamp SVG نسبت به source را کنترل می‌کند. Mermaid CLI/Chromium dependency build است و داخل runtime image محصول لازم نیست.
