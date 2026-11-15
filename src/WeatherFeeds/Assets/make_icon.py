"""Cut the blue weather tile out of the supplied artwork and build a multi-size Windows .ico."""
import sys
from PIL import Image, ImageDraw, ImageFilter

src, out_dir = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGB")
px = im.load()
w, h = im.size

# Tile shape: for each row, span between the leftmost and rightmost clearly-blue pixel.
mask = Image.new("L", (w, h), 0)
d = ImageDraw.Draw(mask)
for y in range(h - 100):  # skip the caption text at the bottom
    xs = [x for x in range(w) if px[x, y][2] - px[x, y][0] > 45]
    if len(xs) > 20:
        d.line([(min(xs), y), (max(xs), y)], fill=255)
mask = mask.filter(ImageFilter.GaussianBlur(0.8))

bbox = mask.getbbox()
tile = im.convert("RGBA")
tile.putalpha(mask)
tile = tile.crop(bbox)

# Centre on a transparent square canvas.
side = max(tile.size)
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.paste(tile, ((side - tile.width) // 2, (side - tile.height) // 2), tile)

master = square.resize((256, 256), Image.LANCZOS)
master.save(f"{out_dir}/icon-256.png")
master.save(f"{out_dir}/WeatherFeeds.ico", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])

# Preview sheet of the small sizes on light and dark backgrounds.
sheet = Image.new("RGBA", (560, 300), (238, 241, 245, 255))
dark = Image.new("RGBA", (560, 150), (13, 17, 23, 255))
sheet.paste(dark, (0, 150))
x = 10
for s in (128, 64, 48, 32, 16):
    ic = square.resize((s, s), Image.LANCZOS)
    sheet.paste(ic, (x, 10), ic)
    sheet.paste(ic, (x, 160), ic)
    x += s + 20
sheet.save(f"{out_dir}/icon-preview.png")
print("bbox", bbox, "side", side)
