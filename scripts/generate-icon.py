from pathlib import Path

from PIL import Image, ImageDraw


SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
CANVAS_SIZE = 1024


def rounded(draw, bounds, radius, fill):
    draw.rounded_rectangle(bounds, radius=radius, fill=fill)


def create_master():
    image = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    rounded(draw, (64, 64, 960, 960), 208, "#202426")

    # A quiet window: the frame stays recognizable at taskbar and tray sizes,
    # while the pause bars communicate that intrusive windows are held back.
    rounded(draw, (190, 228, 834, 796), 112, "#FFFFFF")
    rounded(draw, (246, 346, 778, 740), 58, "#18A596")
    rounded(draw, (246, 286, 306, 322), 18, "#18A596")
    rounded(draw, (326, 286, 386, 322), 18, "#18A596")
    rounded(draw, (406, 286, 466, 322), 18, "#18A596")
    rounded(draw, (392, 438, 466, 646), 30, "#202426")
    rounded(draw, (558, 438, 632, 646), 30, "#202426")

    return image


def main():
    repository_root = Path(__file__).resolve().parents[1]
    asset_directory = repository_root / "src" / "DeskHush.App" / "Assets"
    docs_directory = repository_root / "docs" / "images"
    asset_directory.mkdir(parents=True, exist_ok=True)
    docs_directory.mkdir(parents=True, exist_ok=True)

    master = create_master()
    icon_path = asset_directory / "DeskHush.ico"
    preview_path = docs_directory / "icon.png"
    master.resize((512, 512), Image.Resampling.LANCZOS).save(preview_path, "PNG", optimize=True)
    master.save(icon_path, "ICO", sizes=[(size, size) for size in SIZES])

    print(icon_path)
    print(preview_path)


if __name__ == "__main__":
    main()
