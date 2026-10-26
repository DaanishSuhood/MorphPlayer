"""Draws the MorphPlayer icon: a saffron-orange disc holding a white play triangle.

Writes src/MorphPlayer/Assets/MorphPlayer.ico (all Windows icon sizes) and MorphPlayer-256.png (preview).
Each size is drawn at 8x and downsampled; sizes up to 32 px use a bigger triangle so they stay readable.

    python -I tools/icon/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "MorphPlayer" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SUPERSAMPLE = 8

TOP_COLOR = (255, 176, 0)     # amber, top-left
BOTTOM_COLOR = (232, 84, 0)   # deep saffron, bottom-right
WHITE = (255, 255, 255, 255)


def triangle_core(s: float, simple: bool) -> tuple[list[tuple[float, float]], float]:
    """Vertices of the triangle before rounding, and the rounding radius.

    The finished triangle is this core grown by the radius in every direction, which gives it round
    corners. Its centroid sits on the disc's centre, which reads as centred even though the
    triangle's bounding box is not."""
    half_height = 0.20 * s if simple else 0.17 * s
    length = 0.34 * s if simple else 0.29 * s
    left = 0.5 * s - length / 3
    points = [(left, 0.5 * s - half_height), (left, 0.5 * s + half_height), (left + length, 0.5 * s)]
    return points, 0.045 * s


def render(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    simple = size <= 32

    # Disc filled with a diagonal amber-to-saffron gradient.
    gradient = Image.new("RGBA", (s, s))
    pixels = gradient.load()
    for y in range(0, s, SUPERSAMPLE):
        for x in range(0, s, SUPERSAMPLE):
            t = (x + y) / (2 * s)
            color = tuple(round(a + (b - a) * t) for a, b in zip(TOP_COLOR, BOTTOM_COLOR)) + (255,)
            for dy in range(SUPERSAMPLE):
                for dx in range(SUPERSAMPLE):
                    pixels[x + dx, y + dy] = color
    mask = Image.new("L", (s, s), 0)
    margin = 0.03 * s
    ImageDraw.Draw(mask).ellipse((margin, margin, s - margin, s - margin), fill=255)
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    image.paste(gradient, (0, 0), mask)

    # Rounded triangle: the core polygon, its edges stroked at twice the radius, and a disc on each vertex.
    core, radius = triangle_core(s, simple)
    draw = ImageDraw.Draw(image)
    draw.polygon(core, fill=WHITE)
    draw.line(core + [core[0]], fill=WHITE, width=round(2 * radius), joint="curve")
    for x, y in core:
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=WHITE)

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    images = {size: render(size) for size in SIZES}

    ico_path = ASSETS / "MorphPlayer.ico"
    largest = images[SIZES[-1]]
    largest.save(ico_path, format="ICO", sizes=[(n, n) for n in SIZES],
                 append_images=[images[n] for n in SIZES[:-1]])
    largest.save(ASSETS / "MorphPlayer-256.png")

    with Image.open(ico_path) as ico:
        print(f"{ico_path.relative_to(ROOT)}: sizes {sorted(ico.info['sizes'])}")


if __name__ == "__main__":
    main()
