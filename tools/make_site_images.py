"""Builds site/img from the app's assets and docs/screenshots (run after changing either).

    python tools/make_site_images.py
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "site" / "img"
OUT.mkdir(parents=True, exist_ok=True)

# Screenshots: WebP, at most 1600 px wide.
for name in ["graph", "local-review", "recent-tiles", "side-by-side-diff", "staging", "merge-tool",
             "interactive-rebase", "gitignore", "context-menu"]:
    img = Image.open(ROOT / "docs" / "screenshots" / f"{name}.png").convert("RGB")
    if img.width > 1600:
        img = img.resize((1600, round(img.height * 1600 / img.width)), Image.LANCZOS)
    img.save(OUT / f"{name}.webp", "WEBP", quality=88, method=6)

# The wordmark and the app icon (256 px; the site puts it on a light tile, as its arrow is dark).
logo = Image.open(ROOT / "docs" / "logo.png").convert("RGBA")
logo.save(OUT / "logo.webp", "WEBP", quality=92, method=6)
icon = Image.open(ROOT / "src" / "TotalGit.App" / "Assets" / "totalgit.png").convert("RGBA")
icon.resize((96, 96), Image.LANCZOS).save(OUT / "mark.png")
icon.resize((64, 64), Image.LANCZOS).save(OUT / "favicon.png")
icon.resize((180, 180), Image.LANCZOS).save(OUT / "apple-touch-icon.png")

# Social preview: the graph screenshot under the wordmark on the site's background.
og = Image.new("RGB", (1200, 630), (13, 16, 22))
shot = Image.open(ROOT / "docs" / "screenshots" / "graph.png").convert("RGB")
shot = shot.resize((1040, round(shot.height * 1040 / shot.width)), Image.LANCZOS)
og.paste(shot, (80, 190))
mark = logo.resize((560, round(logo.height * 560 / logo.width)), Image.LANCZOS)
og.paste(mark, (320, 40), mark)
og.save(OUT / "og.png", optimize=True)
print("written", sorted(p.name for p in OUT.iterdir()))
