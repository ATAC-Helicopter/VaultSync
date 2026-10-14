# A large Unity backup sent me looking at exclusions before compression — VaultSync 1.9.0 dev update

Hi everyone — I’m working on VaultSync, an open-source desktop backup application. This update started with a mostly-code Unity project producing a surprisingly large backup.

Looking at the actual contents exposed inconsistent exclusion-preset resolution and saved profiler captures entering source backups. The Unity preset now excludes `.pdata` captures as well as its cache/build rules. Existing archives are not rewritten.

There are a few visible changes in the development UI:

- **Imported projects appear under Projects**, including entries with no local source folder. The Imported label describes origin; linking/restoring a folder preserves it. Missing source folders disable source-dependent actions.
- **Storage charts use native Avalonia controls**, with the usage breakdown retained.
- Missing UI translations are repaired, with resource checks across all 19 maintained languages.

For enabled ZIP compression, Unity scenes, prefabs and metadata now use a denser level. Generated scene/prefab ZIPs were **33.3% smaller**, at roughly five times the encoding time in that small local test. Small metadata ZIPs improved by **8.1%**, but file-header overhead still made the archive larger than its logical contents. These are synthetic measurements, not real-project or upload-speed promises.

I also added Huffman coding to a separate LZ77 prototype: **28.4% smaller YAML blocks and 33.3% smaller code blocks than my first prototype**. DEFLATE still wins on size, and the Python reader is slower. That experiment stays outside the app; normal archives remain ZIP.

The attached images are unmodified native renders of the actual development views on macOS, with synthetic projects and two temporary file backups. They are not conceptual mockups, OS screen grabs or disk-recovery evidence. The chart reports logical payload sizes, not compressed ZIP bytes.

Current local Release checks: **1,181 Core tests, 192 Python tests, zero warnings/errors**. Serviced .NET runtime checks also pass for five private platform builds.

**1.9.0 has not shipped; stable is still 1.8.9.** Device workflows, boot media, measured recovery, platform/upgrades and remaining release gates are open.

[Development PR and evidence](https://github.com/ATAC-Helicopter/VaultSync/pull/736)

What most often makes your code-project backups grow unexpectedly: caches, profiler captures, builds, or tiny metadata files?

<!-- Posting notes: prepared for r/VaultSync. Attach imported-projects, Unity-preset and native-storage-chart PNGs from the gallery manifest. Remove this comment before posting. Add the FG Labs article link only after its route has been deployed and checked; then add the real Reddit URL to the site entry and run devlog:sync. No Reddit submission has been made. -->
