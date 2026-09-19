"""Erzeugt die Icons der App (src/Momentum4.App/Assets) – selbst gezeichnet, ohne fremde Glyphen.

TrayDark.ico  weißer Kopfhörer für dunkle Taskleisten
TrayLight.ico schwarzer Kopfhörer für helle Taskleisten
App.ico       weißer Kopfhörer auf fast schwarzem, abgerundetem Quadrat (Exe, Fenster; passend zum AMOLED-Design)

Aufruf: python tools/make-icons.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / 'src' / 'Momentum4.App' / 'Assets'
BIG = 512


def headphones(color, size=BIG, inset=0.0):
    """Kopfhörer: Bügel als dicker Bogen, zwei abgerundete Ohrmuscheln."""
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size * (1 - 2 * inset)
    o = size * inset
    stroke = s * 0.11
    # Bügel: oberer Halbkreis
    box = [o + s * 0.12, o + s * 0.10, o + s * 0.88, o + s * 0.86]
    d.arc(box, start=180, end=360, fill=color, width=int(stroke))
    # senkrechte Stücke vom Bogen bis zu den Muscheln
    for x in (o + s * 0.12, o + s * 0.88 - stroke):
        d.rectangle([x, o + s * 0.48, x + stroke, o + s * 0.62], fill=color)
    # Ohrmuscheln
    for left in (True, False):
        x0 = o + s * (0.06 if left else 0.70)
        d.rounded_rectangle([x0, o + s * 0.55, x0 + s * 0.24, o + s * 0.92], radius=s * 0.08, fill=color)
    return img


def save_ico(img, name, sizes):
    OUT.mkdir(parents=True, exist_ok=True)
    img.save(OUT / name, format='ICO', sizes=[(n, n) for n in sizes])


tray_sizes = [16, 20, 24, 32, 40, 48, 64]
save_ico(headphones((255, 255, 255, 255)), 'TrayDark.ico', tray_sizes)
save_ico(headphones((0, 0, 0, 255)), 'TrayLight.ico', tray_sizes)

app = Image.new('RGBA', (BIG, BIG), (0, 0, 0, 0))
# Fast schwarz mit hellerer Kante, damit das Symbol auch auf schwarzer Taskleiste Kontur hat.
ImageDraw.Draw(app).rounded_rectangle([0, 0, BIG - 1, BIG - 1], radius=BIG * 0.22, fill=(13, 13, 13, 255), outline=(58, 58, 58, 255), width=int(BIG * 0.03))
app.alpha_composite(headphones((255, 255, 255, 255), inset=0.16))
save_ico(app, 'App.ico', [16, 20, 24, 32, 40, 48, 64, 128, 256])
print('Icons in', OUT)
