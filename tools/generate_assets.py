"""Build launcher tiles from the user supplied Assets/AppIcon.svg mark.

Requires Pillow and CairoSVG. The source SVG is never modified.
"""

from io import BytesIO
from pathlib import Path
from xml.etree import ElementTree

import cairosvg
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
FONT = Path("C:/Windows/Fonts/segoeuib.ttf")
FONT_REGULAR = Path("C:/Windows/Fonts/segoeui.ttf")

source = ElementTree.parse(ASSETS / "AppIcon.svg").getroot()
path_data = source.find("{http://www.w3.org/2000/svg}path").attrib["d"]


def variant(background: str | None, stroke: str, stroke_width: int = 12, inset: bool = False) -> str:
    backdrop = f'<rect width="120" height="120" rx="26" fill="{background}"/>' if background else ""
    mark_open = '<g transform="translate(60 60) scale(0.68) translate(-60 -60)">' if inset else ""
    mark_close = "</g>" if inset else ""
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120">'
        + backdrop
        + mark_open
        + f'<path d="{path_data}" fill="none" stroke="{stroke}" stroke-width="{stroke_width}"'
        + ' stroke-linecap="round" stroke-linejoin="round"/>'
        + mark_close + '</svg>'
    )


variants = {
    "AppIconCreamTile.svg": variant("#EDE7DA", "#202124", stroke_width=15, inset=True),
    "AppIconBlackTile.svg": variant("#111111", "#F3EDE2", stroke_width=15, inset=True),
    "AppIconLight.svg": variant(None, "#F3EDE2", stroke_width=14),
    "AppIconHome.svg": variant(None, "#F3EDE2", stroke_width=16),
}


def mark(size: int, name: str = "AppIconBlackTile.svg") -> Image.Image:
    png = cairosvg.svg2png(bytestring=variants[name].encode(), output_width=size, output_height=size)
    return Image.open(BytesIO(png)).convert("RGBA")


def centered_tile(width: int, height: int, logo_size: int) -> Image.Image:
    image = Image.new("RGBA", (width, height), "#111111")
    logo = mark(logo_size)
    image.alpha_composite(logo, ((width - logo_size) // 2, (height - logo_size) // 2))
    return image


def wide_tile() -> Image.Image:
    image = Image.new("RGBA", (620, 300), "#111111")
    image.alpha_composite(mark(180), (58, 60))
    draw = ImageDraw.Draw(image)
    draw.text((260, 100), "AnotherDSHL", font=ImageFont.truetype(FONT, 45), fill="#F3EDE2")
    draw.text((262, 160), "LAUNCH CONTROL", font=ImageFont.truetype(FONT_REGULAR, 22), fill="#F3EDE2")
    return image


def main() -> None:
    for filename, svg in variants.items():
        (ASSETS / filename).write_text(svg, encoding="utf-8")
    mark(256).save(ASSETS / "AppIcon.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    mark(300).save(ASSETS / "Square150x150Logo.scale-200.png")
    mark(88).save(ASSETS / "Square44x44Logo.scale-200.png")
    mark(24, "AppIconLight.svg").save(ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png")
    mark(48, "AppIconLight.svg").save(ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png")
    mark(50).save(ASSETS / "StoreLogo.png")
    mark(96).save(ASSETS / "LockScreenLogo.scale-200.png")
    wide_tile().save(ASSETS / "Wide310x150Logo.scale-200.png")
    wide_tile().save(ASSETS / "SplashScreen.scale-200.png")


if __name__ == "__main__":
    main()
