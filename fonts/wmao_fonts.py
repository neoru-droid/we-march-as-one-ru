from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._c_m_a_p import CmapSubtable
from fontTools.ttLib.tables._g_l_y_f import Glyph
from PIL import Image, ImageDraw, ImageFont

PointFlag = tuple[int, int, int]
Contour = list[PointFlag]

SORA_INTER_PAIRS: tuple[tuple[str, str], ...] = (
    ("Sora-Regular.ttf", "Inter-Regular.ttf"),
    ("Sora-Bold.ttf", "Inter-Bold.ttf"),
)

DISPLAY_FONTS: tuple[str, ...] = (
    "BigShouldersDisplay-ExtraBold.ttf",
    "BigShouldersDisplay-Black.ttf",
    "Anton-Regular.ttf",
)

HOMOGLYPH_COPIES: tuple[tuple[int, str], ...] = (
    (0x0410, "A"),
    (0x0412, "B"),
    (0x0415, "E"),
    (0x0401, "Edieresis"),
    (0x041A, "K"),
    (0x041C, "M"),
    (0x041D, "H"),
    (0x041E, "O"),
    (0x0420, "P"),
    (0x0421, "C"),
    (0x0422, "T"),
    (0x0425, "X"),
    (0x0430, "a"),
    (0x0435, "e"),
    (0x0451, "edieresis"),
    (0x043E, "o"),
    (0x0440, "p"),
    (0x0441, "c"),
    (0x0443, "y"),
    (0x0445, "x"),
)


@dataclass(frozen=True)
class DisplayMetrics:
    cap: int
    xh: int
    overshoot: int
    lc_overshoot: int
    stem: int
    bar: int
    lsb: int
    h_adv: int
    m_adv: int
    lc_stem: int
    lc_bar: int
    lc_lsb: int
    n_adv: int
    desc_leg: int
    lc_desc_leg: int
    asc_top: int
    p_ymin: int


def _require_file(path: Path) -> Path:
    if not path.is_file():
        raise FileNotFoundError(f"Missing required font file: {path}")
    return path


def _signed_area2(contour: Contour) -> int:
    n = len(contour)
    total = 0
    for i in range(n):
        x0, y0, _ = contour[i]
        x1, y1, _ = contour[(i + 1) % n]
        total += x0 * y1 - x1 * y0
    return total


def _orient(contour: Contour, *, clockwise: bool) -> Contour:
    area2 = _signed_area2(contour)
    is_cw = area2 < 0
    if is_cw == clockwise:
        return list(contour)
    return list(reversed(contour))


def _contours_from_glyph(tt: TTFont, gname: str) -> list[Contour]:
    glyf = tt["glyf"]
    g = glyf[gname]
    if g.isComposite():
        rec = DecomposingRecordingPen(tt.getGlyphSet())
        tt.getGlyphSet()[gname].draw(rec)
        pen = TTGlyphPen(None)
        rec.replay(pen)
        g = pen.glyph()
    pts, end_pts, flags = g.getCoordinates(glyf)
    contours: list[Contour] = []
    start = 0
    for ep in end_pts:
        c = [(int(round(pts[i][0])), int(round(pts[i][1])), int(flags[i] & 1)) for i in range(start, ep + 1)]
        contours.append(c)
        start = ep + 1
    return contours


def _draw_contour_to_pen(pen: TTGlyphPen, raw: Contour) -> None:
    k = next(i for i, p in enumerate(raw) if p[2] == 1)
    pts = raw[k:] + raw[:k]
    n = len(pts)
    pen.moveTo((pts[0][0], pts[0][1]))
    i = 1
    while i < n:
        if pts[i][2] == 1:
            pen.lineTo((pts[i][0], pts[i][1]))
            i += 1
        else:
            offs: list[tuple[int, int]] = []
            while i < n and pts[i][2] == 0:
                offs.append((pts[i][0], pts[i][1]))
                i += 1
            end_pt = (pts[i][0], pts[i][1]) if i < n else (pts[0][0], pts[0][1])
            pen.qCurveTo(*offs, end_pt)
            i += 1
    pen.closePath()


def _glyph_from_contours(glyf_table: object, contours: Iterable[Contour]) -> Glyph:
    pen = TTGlyphPen(None)
    for raw in contours:
        _draw_contour_to_pen(pen, raw)
    glyph = pen.glyph()
    glyph.recalcBounds(glyf_table)
    return glyph


def _poly(points: list[tuple[int, int]], *, clockwise: bool = True) -> Contour:
    return _orient([(int(round(x)), int(round(y)), 1) for x, y in points], clockwise=clockwise)


def _mirror_x_contours(contours: list[Contour], width: int) -> list[Contour]:
    out: list[Contour] = []
    for c in contours:
        flipped = [(int(round(width - x)), y, on) for x, y, on in reversed(c)]
        out.append(flipped)
    return out


def _scale_contours(
    contours: list[Contour], sx: float, sy: float, dx: float = 0.0, dy: float = 0.0
) -> list[Contour]:
    flip = (sx * sy) < 0
    out: list[Contour] = []
    for c in contours:
        seq = reversed(c) if flip else c
        out.append([(int(round(x * sx + dx)), int(round(y * sy + dy)), on) for x, y, on in seq])
    return out


def _extract_metrics(tt: TTFont) -> DisplayMetrics:
    cmap = tt.getBestCmap()
    glyf = tt["glyf"]
    hmtx = tt["hmtx"].metrics
    os2 = tt["OS/2"]

    def _b(ch: str) -> tuple[int, int, int, int]:
        g = glyf[cmap[ord(ch)]]
        g.recalcBounds(glyf)
        return g.xMin, g.yMin, g.xMax, g.yMax

    ix0, _, ix1, _ = _b("I")
    hx0, _, _, _ = _b("H")
    _, oy0, _, _ = _b("O")
    lx0, _, lx1, _ = _b("l")
    nx0, _, _, _ = _b("n")
    _, o_lc_y0, _, _ = _b("o")
    _, _, _, b_y1 = _b("b")
    _, p_y0, _, _ = _b("p")

    f_contours = _contours_from_glyph(tt, cmap[ord("F")])
    cap = int(os2.sCapHeight)
    xh = int(os2.sxHeight)
    top_ys = sorted({y for x, y, _ in f_contours[0] if y > cap // 2 and y < cap}, reverse=True)
    bar = cap - top_ys[0] if top_ys else round(0.15 * cap)

    o_lc_contours = _contours_from_glyph(tt, cmap[ord("o")])
    inner_min_y = min(y for _, y, _ in o_lc_contours[1])
    lc_bar = inner_min_y - o_lc_y0

    return DisplayMetrics(
        cap=cap,
        xh=xh,
        overshoot=max(0, -oy0),
        lc_overshoot=max(0, -o_lc_y0),
        stem=ix1 - ix0,
        bar=bar,
        lsb=hx0,
        h_adv=hmtx[cmap[ord("H")]][0],
        m_adv=hmtx[cmap[ord("M")]][0],
        lc_stem=lx1 - lx0,
        lc_bar=lc_bar,
        lc_lsb=nx0,
        n_adv=hmtx[cmap[ord("n")]][0],
        desc_leg=round(0.175 * cap),
        lc_desc_leg=min(abs(p_y0), round(0.20 * xh)),
        asc_top=b_y1,
        p_ymin=p_y0,
    )


def _build_ge(top: int, stem: int, bar: int, lsb: int, adv: int) -> tuple[list[Contour], int]:
    xr = adv - round(0.55 * lsb)
    pts = [(lsb, 0), (lsb, top), (xr, top), (xr, top - bar), (lsb + stem, top - bar), (lsb + stem, 0)]
    return [_poly(pts, clockwise=True)], adv


def _build_pe(top: int, stem: int, bar: int, lsb: int, adv: int) -> tuple[list[Contour], int]:
    xr = adv - lsb
    pts = [
        (lsb, 0),
        (lsb, top),
        (xr, top),
        (xr, 0),
        (xr - stem, 0),
        (xr - stem, top - bar),
        (lsb + stem, top - bar),
        (lsb + stem, 0),
    ]
    return [_poly(pts, clockwise=True)], adv


def _build_en_lc(top: int, stem: int, bar: int, lsb: int, adv: int) -> tuple[list[Contour], int]:
    xr = adv - lsb
    mid_lo = round(0.42 * top)
    mid_hi = mid_lo + bar
    pts = [
        (lsb, 0),
        (lsb, top),
        (lsb + stem, top),
        (lsb + stem, mid_hi),
        (xr - stem, mid_hi),
        (xr - stem, top),
        (xr, top),
        (xr, 0),
        (xr - stem, 0),
        (xr - stem, mid_lo),
        (lsb + stem, mid_lo),
        (lsb + stem, 0),
    ]
    return [_poly(pts, clockwise=True)], adv


def _build_te_lc(top: int, stem: int, bar: int, adv: int) -> tuple[list[Contour], int]:
    pad = round(0.03 * adv)
    xl = pad
    xr = adv - pad
    sl = (adv - stem) // 2
    sr = sl + stem
    pts = [
        (sl, 0),
        (sl, top - bar),
        (xl, top - bar),
        (xl, top),
        (xr, top),
        (xr, top - bar),
        (sr, top - bar),
        (sr, 0),
    ]
    return [_poly(pts, clockwise=True)], adv


def _build_sha(
    top: int, stem: int, bar: int, lsb: int, body_w: int, spur_ext: int = 0, leg_depth: int = 0
) -> tuple[list[Contour], int]:
    gap = max(round(0.28 * stem), (body_w - 3 * stem) // 2)
    x0 = lsb
    x1 = x0 + stem
    x2 = x1 + gap
    x3 = x2 + stem
    x4 = x3 + gap
    x5 = x4 + stem
    pts: list[tuple[int, int]] = [
        (x0, 0),
        (x0, top),
        (x1, top),
        (x1, bar),
        (x2, bar),
        (x2, top),
        (x3, top),
        (x3, bar),
        (x4, bar),
        (x4, top),
        (x5, top),
    ]
    if spur_ext > 0 and leg_depth > 0:
        sx_r = x5 + spur_ext
        spur_stem = max(round(0.86 * stem), stem - 24)
        pts.extend([(x5, bar), (sx_r, bar), (sx_r, -leg_depth), (sx_r - spur_stem, -leg_depth), (sx_r - spur_stem, 0)])
        adv = sx_r + max(24, round(0.45 * lsb))
    else:
        pts.append((x5, 0))
        adv = x5 + lsb
    return [_poly(pts, clockwise=True)], adv


def _build_tse(
    top: int, stem: int, bar: int, lsb: int, base_adv: int, spur_ext: int, leg_depth: int
) -> tuple[list[Contour], int]:
    x0 = lsb
    x1 = x0 + stem
    x3 = base_adv - lsb
    x2 = x3 - stem
    sx_r = x3 + spur_ext
    spur_stem = max(round(0.86 * stem), stem - 24)
    pts = [
        (x0, 0),
        (x0, top),
        (x1, top),
        (x1, bar),
        (x2, bar),
        (x2, top),
        (x3, top),
        (x3, bar),
        (sx_r, bar),
        (sx_r, -leg_depth),
        (sx_r - spur_stem, -leg_depth),
        (sx_r - spur_stem, 0),
    ]
    adv = sx_r + max(24, round(0.45 * lsb))
    return [_poly(pts, clockwise=True)], adv


def _build_de(
    top: int, stem: int, bar: int, lsb: int, base_adv: int, leg_depth: int
) -> tuple[list[Contour], int]:
    overhang = round(0.36 * stem)
    leg_w = max(round(0.86 * stem), stem - 24)
    bx0 = max(24, round(0.55 * lsb))
    body_l_bot = bx0 + overhang
    body_l_top = body_l_bot + round(0.24 * stem)
    body_r = body_l_bot + (base_adv - 2 * lsb) + round(0.14 * stem)
    bx1 = body_r + overhang
    outer = _poly(
        [
            (bx0, -leg_depth),
            (bx0, bar),
            (body_l_bot, bar),
            (body_l_top, top),
            (body_r, top),
            (body_r, bar),
            (bx1, bar),
            (bx1, -leg_depth),
            (bx1 - leg_w, -leg_depth),
            (bx1 - leg_w, 0),
            (bx0 + leg_w, 0),
            (bx0 + leg_w, -leg_depth),
        ],
        clockwise=True,
    )
    inner = _poly(
        [
            (body_l_bot + round(0.96 * stem), bar),
            (body_r - stem, bar),
            (body_r - stem, top - bar),
            (body_l_top + round(0.94 * stem), top - bar),
        ],
        clockwise=False,
    )
    adv = bx1 + bx0
    return [outer, inner], adv


def _build_el(
    top: int, stem: int, bar: int, lsb: int, base_adv: int, overshoot: int
) -> tuple[list[Contour], int]:
    foot_l = max(20, round(0.38 * lsb))
    xr = base_adv - round(0.75 * lsb) + round(0.42 * stem)
    roof_l = foot_l + round(1.08 * stem)
    foot_r = foot_l + round(0.98 * stem)
    c: Contour = _orient(
        [
            (foot_l, 0, 1),
            (foot_l, bar, 1),
            (foot_l + round(0.30 * stem), bar, 1),
            (foot_l + round(0.76 * stem), bar, 0),
            (foot_l + round(0.90 * stem), round(0.42 * top), 1),
            (roof_l, top, 1),
            (xr, top, 1),
            (xr, 0, 1),
            (xr - stem, 0, 1),
            (xr - stem, top - bar, 1),
            (roof_l + round(0.88 * stem), top - bar, 1),
            (foot_l + round(1.78 * stem), round(0.38 * top), 1),
            (foot_l + round(1.56 * stem), 0, 0),
            (foot_r, 0, 1),
        ],
        clockwise=True,
    )
    _ = overshoot
    return [c], xr + round(0.85 * lsb)


def _build_che(top: int, stem: int, bar: int, lsb: int, adv: int) -> tuple[list[Contour], int]:
    xr = adv - lsb
    waist_bot = round(0.39 * top)
    waist_top = waist_bot + bar
    rad_out = round(1.15 * stem)
    rad_in = max(round(0.42 * stem), 60)
    c: Contour = _orient(
        [
            (lsb, waist_bot + rad_out, 1),
            (lsb, top, 1),
            (lsb + stem, top, 1),
            (lsb + stem, waist_top + rad_in, 1),
            (lsb + stem, waist_top, 0),
            (lsb + stem + rad_in, waist_top, 1),
            (xr - stem, waist_top, 1),
            (xr - stem, top, 1),
            (xr, top, 1),
            (xr, 0, 1),
            (xr - stem, 0, 1),
            (xr - stem, waist_bot, 1),
            (lsb + rad_out, waist_bot, 1),
            (lsb, waist_bot, 0),
        ],
        clockwise=True,
    )
    return [c], adv


def _build_zhe_from_ka(ka_contour: Contour, condense: float, margin: int) -> tuple[list[Contour], int]:
    lsb_x = ka_contour[0][0]
    stem_r = ka_contour[-1][0]
    mid_x = 0.5 * (lsb_x + stem_r)
    right_raw = ka_contour[2:]
    right_pts: Contour = []
    for x, y, on in right_raw:
        nx = stem_r + (x - stem_r) * condense if x > stem_r else float(x)
        right_pts.append((int(round(nx)), y, on))
    left_pts: Contour = [(int(round(2.0 * mid_x - x)), y, on) for x, y, on in reversed(right_pts)]
    combined = right_pts + left_pts
    min_x = min(x for x, _, _ in combined)
    dx = margin - min_x
    shifted = _orient([(x + dx, y, on) for x, y, on in combined], clockwise=True)
    max_x = max(x for x, _, _ in shifted)
    return [shifted], max_x + margin


def _rotate_top_pair_first(contour: Contour, cap: int) -> Contour:
    pts = _orient(contour, clockwise=True)
    n = len(pts)
    for i in range(n):
        if pts[i][1] == cap and pts[(i + 1) % n][1] == cap:
            return [pts[(i + k) % n] for k in range(n)]
    return pts


def _soft_sign_from_pe(tt: TTFont, m: DisplayMetrics) -> tuple[list[Contour], int]:
    cmap = tt.getBestCmap()
    p_cs = _contours_from_glyph(tt, cmap[ord("P")])
    p_adv = tt["hmtx"].metrics[cmap[ord("P")]][0]
    flipped = _scale_contours(p_cs, 1.0, -1.0, 0.0, float(m.cap))
    bowl_top_orig = max(y for x, y, _ in flipped[0] if x > m.lsb + m.stem + 20)
    target_top = round(0.60 * m.cap)
    delta = bowl_top_orig - target_top

    def _remap_y(y: int) -> int:
        if y <= m.bar or y >= bowl_top_orig + 20:
            return y
        if y >= bowl_top_orig - m.bar:
            return y - delta
        span = (bowl_top_orig - m.bar) - m.bar
        t = (y - m.bar) / span
        return int(round(m.bar + t * (target_top - 2 * m.bar)))

    out0 = _rotate_top_pair_first([(x, _remap_y(y), on) for x, y, on in flipped[0]], m.cap)
    out1 = _orient([(x, _remap_y(y), on) for x, y, on in flipped[1]], clockwise=False)
    return [out0, out1], p_adv


def _be_from_soft_sign(soft_cs: list[Contour], soft_adv: int, m: DisplayMetrics) -> tuple[list[Contour], int]:
    outer = _rotate_top_pair_first(soft_cs[0], m.cap)
    arm_r = max(x for x, _, _ in outer) - 8
    xl, xr = outer[0][0], outer[1][0]
    rebuilt: Contour = [
        (xl, m.cap, 1),
        (arm_r, m.cap, 1),
        (arm_r, m.cap - m.bar, 1),
        (xr, m.cap - m.bar, 1),
        *outer[2:],
    ]
    return [_orient(rebuilt, clockwise=True), soft_cs[1]], soft_adv + 10


def _hard_sign_from_soft_sign(
    soft_cs: list[Contour], soft_adv: int, cap: int, bar: int, stem: int
) -> tuple[list[Contour], int]:
    ext = round(0.72 * stem)
    shifted = _scale_contours(soft_cs, 1.0, 1.0, float(ext), 0.0)
    outer = _rotate_top_pair_first(shifted[0], cap)
    xl, xr = outer[0][0], outer[1][0]
    serif_l = max(16, xl - ext)
    rebuilt: Contour = [
        (xl, cap - bar, 1),
        (serif_l, cap - bar, 1),
        (serif_l, cap, 1),
        (xr, cap, 1),
        *outer[2:],
    ]
    return [_orient(rebuilt, clockwise=True), shifted[1]], soft_adv + ext


def _yery_from_soft_sign(
    soft_cs: list[Contour], top: int, stem: int, lsb: int
) -> tuple[list[Contour], int]:
    stem_r = lsb + stem
    cond: list[Contour] = []
    for idx, c in enumerate(soft_cs):
        pts = [(int(round(stem_r + (x - stem_r) * 0.84)) if x > stem_r else x, y, on) for x, y, on in c]
        cond.append(_orient(pts, clockwise=(idx == 0)))
    bowl_max_x = max(x for x, _, _ in cond[0])
    gap = max(round(0.30 * stem), 76)
    ix0 = bowl_max_x + gap
    ix1 = ix0 + stem
    i_bar = _poly([(ix0, 0), (ix0, top), (ix1, top), (ix1, 0)], clockwise=True)
    return [cond[0], cond[1], i_bar], ix1 + lsb


def _scale_cap_to_xh(contours: list[Contour], adv: int, cap: int, xh: int, sx: float = 0.96) -> tuple[list[Contour], int]:
    sy = xh / cap
    scaled = _scale_contours(contours, sx, sy)
    out = [_orient(c, clockwise=(i == 0 or _signed_area2(contours[i]) < 0)) for i, c in enumerate(scaled)]
    return out, int(round(adv * sx))


def _build_curved_e_or_ze(
    top: int, stem: int, bar: int, lsb: int, adv: int, overshoot: int, *, pinch_waist: bool
) -> tuple[list[Contour], int]:
    y_lo = -overshoot
    y_hi = top + overshoot
    xl = max(36, round(0.82 * lsb))
    xr = adv - xl
    r_out = min((xr - xl) // 2 - 4, round(0.27 * top))
    in_l = xl + stem
    in_r = xr - stem
    in_lo = y_lo + bar
    in_hi = y_hi - bar
    r_in = max(24, min((in_r - in_l) // 2 - 4, r_out - round(0.68 * bar)))

    mid_bar = round(0.84 * bar)
    mid_lo = round(0.51 * top) - mid_bar // 2
    mid_hi = mid_lo + mid_bar
    term_hi = max(mid_hi + round(0.58 * bar), min(in_hi - r_in // 2, round(0.70 * top)))
    term_lo = min(mid_lo - round(0.58 * bar), max(in_lo + r_in // 2, round(0.30 * top)))
    tongue_l = xl + (round(0.92 * stem) if pinch_waist else round(0.45 * stem))

    right_wall: Contour
    if pinch_waist:
        waist_x = xr - round(0.68 * stem)
        waist_y = (mid_lo + mid_hi) // 2
        r_w = round(0.85 * bar)
        right_wall = [
            (xr, mid_hi + r_w, 1),
            (xr, mid_hi + 16, 0),
            (waist_x, waist_y + 20, 1),
            (waist_x, waist_y - 20, 1),
            (xr, mid_lo - 16, 0),
            (xr, mid_lo - r_w, 1),
        ]
    else:
        right_wall = []

    pts: Contour = [
        (xl, term_hi, 1),
        (xl, y_hi - r_out, 1),
        (xl, y_hi, 0),
        (xl + r_out, y_hi, 1),
        (xr - r_out, y_hi, 1),
        (xr, y_hi, 0),
        (xr, y_hi - r_out, 1),
        *right_wall,
        (xr, y_lo + r_out, 1),
        (xr, y_lo, 0),
        (xr - r_out, y_lo, 1),
        (xl + r_out, y_lo, 1),
        (xl, y_lo, 0),
        (xl, y_lo + r_out, 1),
        (xl, term_lo, 1),
        (in_l, term_lo, 1),
        (in_l, in_lo + r_in, 1),
        (in_l, in_lo, 0),
        (in_l + r_in, in_lo, 1),
        (in_r - r_in, in_lo, 1),
        (in_r, in_lo, 0),
        (in_r, in_lo + r_in, 1),
        (in_r, mid_lo, 1),
        (tongue_l, mid_lo, 1),
        (tongue_l, mid_hi, 1),
        (in_r, mid_hi, 1),
        (in_r, in_hi - r_in, 1),
        (in_r, in_hi, 0),
        (in_r - r_in, in_hi, 1),
        (in_l + r_in, in_hi, 1),
        (in_l, in_hi, 0),
        (in_l, in_hi - r_in, 1),
        (in_l, term_hi, 1),
    ]
    return [_orient(pts, clockwise=True)], adv


def _yu_from_oval(
    oval_cs: list[Contour], oval_adv: int, top: int, stem: int, bar: int, lsb: int
) -> tuple[list[Contour], int]:
    oval_min_x = min(x for x, _, _ in oval_cs[0])
    bridge_w = max(round(0.48 * stem), 110)
    dx = lsb + stem + bridge_w - oval_min_x
    shifted_o = _scale_contours(oval_cs, 1.0, 1.0, float(dx), 0.0)
    o_outer = shifted_o[0]
    o_left = min(x for x, _, _ in o_outer)
    mid_lo = round(0.43 * top)
    mid_hi = mid_lo + bar

    # Fuse left I-stem + crossbar directly into the left vertical wall of O's outer contour
    fused: Contour = []
    spliced = False
    for x, y, on in o_outer:
        if not spliced and abs(x - o_left) <= 12 and (mid_lo - 260) <= y <= (mid_hi + 260):
            fused.extend(
                [
                    (o_left, mid_lo, 1),
                    (lsb + stem, mid_lo, 1),
                    (lsb + stem, 0, 1),
                    (lsb, 0, 1),
                    (lsb, top, 1),
                    (lsb + stem, top, 1),
                    (lsb + stem, mid_hi, 1),
                    (o_left, mid_hi, 1),
                ]
            )
            spliced = True
        elif abs(x - o_left) <= 12 and mid_lo <= y <= mid_hi:
            continue
        else:
            fused.append((x, y, on))
    return [_orient(fused, clockwise=True), _orient(shifted_o[1], clockwise=False)], oval_adv + dx


def _ef_from_oval(
    oval_cs: list[Contour], bowl_lo: int, bowl_hi: int, stem_lo: int, stem_hi: int, stem: int, lsb: int
) -> tuple[list[Contour], int]:
    oy0 = min(y for _, y, _ in oval_cs[0])
    oy1 = max(y for _, y, _ in oval_cs[0])
    sy = (bowl_hi - bowl_lo) / (oy1 - oy0)
    scaled = _scale_contours(oval_cs, 0.92, sy, 0.0, bowl_lo - oy0 * sy)

    ox0 = min(x for x, _, _ in scaled[0])
    ox1 = max(x for x, _, _ in scaled[0])
    omid = 0.5 * (ox0 + ox1)
    spread = round(0.76 * stem)

    # Left lobe anchored at lsb, central stem in middle, right lobe shifted by +spread
    dx_base = lsb - ox0
    stem_cx = int(round(omid + dx_base + 0.5 * spread))
    sl = stem_cx - stem // 2
    sr = sl + stem

    # Build clean symmetric stadium Ф / ф outer + 2 D-counters using oval curvature metrics
    rx = int(round(sr + (omid - ox0)))
    r_arc = min((bowl_hi - bowl_lo) // 2 - 10, (sl - lsb) - 10)
    bar_h = max(round(0.78 * stem), (bowl_hi - bowl_lo) // 5)
    r_in = max(24, r_arc - round(0.85 * stem))

    outer = _orient(
        [
            (sl, stem_lo, 1),
            (sl, bowl_lo, 1),
            (lsb + r_arc, bowl_lo, 1),
            (lsb, bowl_lo, 0),
            (lsb, bowl_lo + r_arc, 1),
            (lsb, bowl_hi - r_arc, 1),
            (lsb, bowl_hi, 0),
            (lsb + r_arc, bowl_hi, 1),
            (sl, bowl_hi, 1),
            (sl, stem_hi, 1),
            (sr, stem_hi, 1),
            (sr, bowl_hi, 1),
            (rx - r_arc, bowl_hi, 1),
            (rx, bowl_hi, 0),
            (rx, bowl_hi - r_arc, 1),
            (rx, bowl_lo + r_arc, 1),
            (rx, bowl_lo, 0),
            (rx - r_arc, bowl_lo, 1),
            (sr, bowl_lo, 1),
            (sr, stem_lo, 1),
        ],
        clockwise=True,
    )
    in_l_x = lsb + round(0.92 * stem)
    in_r_x = rx - round(0.92 * stem)
    in_lo = bowl_lo + bar_h
    in_hi = bowl_hi - bar_h
    hole_l = _orient(
        [
            (sl, in_lo, 1),
            (sl, in_hi, 1),
            (in_l_x + r_in, in_hi, 1),
            (in_l_x, in_hi, 0),
            (in_l_x, in_hi - r_in, 1),
            (in_l_x, in_lo + r_in, 1),
            (in_l_x, in_lo, 0),
            (in_l_x + r_in, in_lo, 1),
        ],
        clockwise=False,
    )
    hole_r = _orient(
        [
            (sr, in_lo, 1),
            (in_r_x - r_in, in_lo, 1),
            (in_r_x, in_lo, 0),
            (in_r_x, in_lo + r_in, 1),
            (in_r_x, in_hi - r_in, 1),
            (in_r_x, in_hi, 0),
            (in_r_x - r_in, in_hi, 1),
            (sr, in_hi, 1),
        ],
        clockwise=False,
    )
    return [outer, hole_l, hole_r], rx + lsb


def _be_lc_from_o(tt: TTFont, m: DisplayMetrics) -> tuple[list[Contour], int]:
    cmap = tt.getBestCmap()
    raw_o = _contours_from_glyph(tt, cmap[ord("o")])
    o_adv = tt["hmtx"].metrics[cmap[ord("o")]][0]
    oy0 = min(y for _, y, _ in raw_o[0])
    oy1 = max(y for _, y, _ in raw_o[0])
    bowl_hi = min(oy1, m.asc_top - round(1.85 * m.lc_bar))
    sy = (bowl_hi - oy0) / (oy1 - oy0)
    o_cs = _scale_contours(raw_o, 1.0, sy, 0.0, oy0 * (1.0 - sy))

    ox0 = min(x for x, _, _ in o_cs[0])
    ox1 = max(x for x, _, _ in o_cs[0])
    top_y = m.asc_top
    arm_r = ox1 - 16
    shoulder_lo = round(0.60 * bowl_hi)
    r_out = round(0.95 * m.lc_stem)
    r_in = round(0.42 * m.lc_stem)

    rebuilt: Contour = []
    spliced = False
    for x, y, on in o_cs[0]:
        if not spliced and x <= ox0 + 30 and y >= shoulder_lo:
            rebuilt.extend(
                [
                    (ox0, shoulder_lo, 1),
                    (ox0, top_y - r_out, 1),
                    (ox0, top_y, 0),
                    (ox0 + r_out, top_y, 1),
                    (arm_r, top_y, 1),
                    (arm_r, top_y - m.lc_bar, 1),
                    (ox0 + m.lc_stem + r_in, top_y - m.lc_bar, 1),
                    (ox0 + m.lc_stem, top_y - m.lc_bar, 0),
                    (ox0 + m.lc_stem, top_y - m.lc_bar - r_in, 1),
                    (ox0 + m.lc_stem, bowl_hi - round(0.32 * m.lc_bar), 1),
                ]
            )
            spliced = True
        elif x < (ox0 + m.lc_stem + 40) and y > shoulder_lo:
            continue
        else:
            rebuilt.append((x, y, on))
    return [_orient(rebuilt, clockwise=True), _orient(o_cs[1], clockwise=False)], o_adv


def _with_breve_from_source(
    base_cs: list[Contour], base_adv: int, breve_glyph_cs: list[Contour], y_threshold: int
) -> tuple[list[Contour], int]:
    breve_parts = [c for c in breve_glyph_cs if min(y for _, y, _ in c) > y_threshold]
    bx0 = min(x for c in breve_parts for x, _, _ in c)
    bx1 = max(x for c in breve_parts for x, _, _ in c)
    gx0 = min(x for c in base_cs for x, _, _ in c)
    gx1 = max(x for c in base_cs for x, _, _ in c)
    dx = int(round(0.5 * (gx0 + gx1) - 0.5 * (bx0 + bx1)))
    shifted_breve = [_orient([(x + dx, y, on) for x, y, on in c], clockwise=True) for c in breve_parts]
    return list(base_cs) + shifted_breve, base_adv


def _build_checkmark(m: DisplayMetrics) -> tuple[list[Contour], int]:
    t = round(0.76 * m.stem)
    x0 = m.lsb
    x_mid = x0 + round(0.34 * m.cap)
    x_end = x0 + round(0.86 * m.cap)
    y_left = round(0.46 * m.cap)
    y_bot = round(0.10 * m.cap)
    y_top = round(0.84 * m.cap)
    pts = [
        (x_mid, y_bot),
        (x0, y_left),
        (x0 + round(0.72 * t), y_left + round(0.72 * t)),
        (x_mid + round(0.04 * t), y_bot + round(1.38 * t)),
        (x_end - round(0.72 * t), y_top),
        (x_end, y_top - round(0.72 * t)),
    ]
    return [_poly(pts, clockwise=True)], x_end + m.lsb


def _synthesize_display_glyphs(tt: TTFont) -> dict[int, tuple[list[Contour], int]]:
    cmap = tt.getBestCmap()
    hmtx = tt["hmtx"].metrics
    m = _extract_metrics(tt)
    glyphs: dict[int, tuple[list[Contour], int]] = {}

    for cp, src_name in HOMOGLYPH_COPIES:
        glyphs[cp] = (_contours_from_glyph(tt, src_name), hmtx[src_name][0])

    # И, Й, Я from mirrored N and R
    n_adv = hmtx[cmap[ord("N")]][0]
    i_cap_cs = _mirror_x_contours(_contours_from_glyph(tt, cmap[ord("N")]), n_adv)
    glyphs[0x0418] = (i_cap_cs, n_adv)
    glyphs[0x0419] = _with_breve_from_source(
        i_cap_cs, n_adv, _contours_from_glyph(tt, cmap[0x016C]), m.cap - 40
    )

    r_adv = hmtx[cmap[ord("R")]][0]
    ya_cap_cs = _mirror_x_contours(_contours_from_glyph(tt, cmap[ord("R")]), r_adv)
    glyphs[0x042F] = (ya_cap_cs, r_adv)
    glyphs[0x044F] = _scale_cap_to_xh(ya_cap_cs, r_adv, m.cap, m.xh, sx=0.95)

    # и, й from И scaled to xHeight
    i_lc_cs, i_lc_adv = _scale_cap_to_xh(i_cap_cs, n_adv, m.cap, m.xh, sx=0.95)
    glyphs[0x0438] = (i_lc_cs, i_lc_adv)
    glyphs[0x0439] = _with_breve_from_source(
        i_lc_cs, i_lc_adv, _contours_from_glyph(tt, cmap[0x016D]), m.xh - 40
    )

    # к (clamped native k), Ж, ж
    k_lc_raw = _contours_from_glyph(tt, cmap[ord("k")])[0]
    k_lc_clamped: Contour = []
    for x, y, on in k_lc_raw:
        ny = min(y, m.xh)
        if not k_lc_clamped or (x, ny) != (k_lc_clamped[-1][0], k_lc_clamped[-1][1]):
            k_lc_clamped.append((x, ny, on))
    glyphs[0x043A] = ([_orient(k_lc_clamped, clockwise=True)], hmtx[cmap[ord("k")]][0])
    glyphs[0x0416] = _build_zhe_from_ka(
        _contours_from_glyph(tt, cmap[ord("K")])[0], condense=0.78, margin=max(26, m.lsb // 2)
    )
    glyphs[0x0436] = _build_zhe_from_ka(k_lc_clamped, condense=0.78, margin=max(24, m.lc_lsb // 2))

    # У from native lowercase y shifted/scaled onto [-overshoot, cap]
    y_lc_cs = _contours_from_glyph(tt, cmap[ord("y")])
    y_lc_adv = hmtx[cmap[ord("y")]][0]
    y0 = min(y for _, y, _ in y_lc_cs[0])
    y1 = max(y for _, y, _ in y_lc_cs[0])
    sy_u = (m.cap + m.overshoot) / (y1 - y0)
    u_cap_cs = _scale_contours(y_lc_cs, 1.05, sy_u, 0.0, -m.overshoot - y0 * sy_u)
    glyphs[0x0423] = ([_orient(u_cap_cs[0], clockwise=True)], int(round(y_lc_adv * 1.05)))

    # в, м from B and M scaled to xHeight
    glyphs[0x0432] = _scale_cap_to_xh(
        _contours_from_glyph(tt, cmap[ord("B")]), hmtx[cmap[ord("B")]][0], m.cap, m.xh, sx=0.95
    )
    glyphs[0x043C] = _scale_cap_to_xh(
        _contours_from_glyph(tt, cmap[ord("M")]), m.m_adv, m.cap, m.xh, sx=0.96
    )

    # Ь, Б, Ъ, Ы and ь, ъ, ы, б
    soft_cs, soft_adv = _soft_sign_from_pe(tt, m)
    glyphs[0x042C] = (soft_cs, soft_adv)
    glyphs[0x0411] = _be_from_soft_sign(soft_cs, soft_adv, m)
    hard_cs, hard_adv = _hard_sign_from_soft_sign(soft_cs, soft_adv, m.cap, m.bar, m.stem)
    glyphs[0x042A] = (hard_cs, hard_adv)
    yery_cs, yery_adv = _yery_from_soft_sign(soft_cs, m.cap, m.stem, m.lsb)
    glyphs[0x042B] = (yery_cs, yery_adv)

    glyphs[0x044C] = _scale_cap_to_xh(soft_cs, soft_adv, m.cap, m.xh, sx=0.95)
    glyphs[0x044A] = _scale_cap_to_xh(hard_cs, hard_adv, m.cap, m.xh, sx=0.95)
    glyphs[0x044B] = _scale_cap_to_xh(yery_cs, yery_adv, m.cap, m.xh, sx=0.95)
    glyphs[0x0431] = _be_lc_from_o(tt, m)

    # З, з, Э, э
    c_adv = hmtx[cmap[ord("C")]][0]
    lc_c_adv = hmtx[cmap[ord("c")]][0]
    glyphs[0x0417] = _build_curved_e_or_ze(
        m.cap, m.stem, m.bar, m.lsb, c_adv, m.overshoot, pinch_waist=True
    )
    glyphs[0x042D] = _build_curved_e_or_ze(
        m.cap, m.stem, m.bar, m.lsb, c_adv, m.overshoot, pinch_waist=False
    )
    glyphs[0x0437] = _build_curved_e_or_ze(
        m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, lc_c_adv, m.lc_overshoot, pinch_waist=True
    )
    glyphs[0x044D] = _build_curved_e_or_ze(
        m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, lc_c_adv, m.lc_overshoot, pinch_waist=False
    )

    # Ю, ю, Ф, ф
    o_cap_cs = _contours_from_glyph(tt, cmap[ord("O")])
    o_lc_cs = _contours_from_glyph(tt, cmap[ord("o")])
    glyphs[0x042E] = _yu_from_oval(o_cap_cs, hmtx[cmap[ord("O")]][0], m.cap, m.stem, m.bar, m.lsb)
    glyphs[0x044E] = _yu_from_oval(
        o_lc_cs, hmtx[cmap[ord("o")]][0], m.xh, m.lc_stem, m.lc_bar, m.lc_lsb
    )
    bowl_pad = round(0.11 * m.cap)
    glyphs[0x0424] = _ef_from_oval(o_cap_cs, bowl_pad, m.cap - bowl_pad, 0, m.cap, m.stem, m.lsb)
    glyphs[0x0444] = _ef_from_oval(
        o_lc_cs, -m.lc_overshoot, m.xh + m.lc_overshoot, m.p_ymin, m.asc_top, m.lc_stem, m.lc_lsb
    )

    # Modular geometric letters: Г/г, П/п, н, т, Л/л, Д/д, Ц/ц, Ч/ч, Ш/ш, Щ/щ
    f_adv = hmtx[cmap[ord("F")]][0]
    glyphs[0x0413] = _build_ge(m.cap, m.stem, m.bar, m.lsb, f_adv)
    glyphs[0x0433] = _build_ge(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, round(0.94 * f_adv))
    glyphs[0x041F] = _build_pe(m.cap, m.stem, m.bar, m.lsb, m.h_adv)
    glyphs[0x043F] = _build_pe(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv)
    glyphs[0x043D] = _build_en_lc(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv)
    glyphs[0x0442] = _build_te_lc(m.xh, m.lc_stem, m.lc_bar, hmtx[cmap[ord("T")]][0])

    glyphs[0x041B] = _build_el(m.cap, m.stem, m.bar, m.lsb, m.h_adv, m.overshoot)
    glyphs[0x043B] = _build_el(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv, m.lc_overshoot)

    glyphs[0x0414] = _build_de(m.cap, m.stem, m.bar, m.lsb, m.h_adv, m.desc_leg)
    glyphs[0x0434] = _build_de(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv, m.lc_desc_leg)

    spur = round(0.42 * m.stem)
    lc_spur = round(0.40 * m.lc_stem)
    glyphs[0x0426] = _build_tse(m.cap, m.stem, m.bar, m.lsb, m.h_adv, spur, m.desc_leg)
    glyphs[0x0446] = _build_tse(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv, lc_spur, m.lc_desc_leg)

    glyphs[0x0427] = _build_che(m.cap, m.stem, m.bar, m.lsb, m.h_adv)
    glyphs[0x0447] = _build_che(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, m.n_adv)

    sha_w = m.m_adv - 2 * m.lsb
    lc_sha_w = round(0.95 * sha_w)
    glyphs[0x0428] = _build_sha(m.cap, m.stem, m.bar, m.lsb, sha_w)
    glyphs[0x0448] = _build_sha(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, lc_sha_w)
    glyphs[0x0429] = _build_sha(m.cap, m.stem, m.bar, m.lsb, sha_w, spur, m.desc_leg)
    glyphs[0x0449] = _build_sha(m.xh, m.lc_stem, m.lc_bar, m.lc_lsb, lc_sha_w, lc_spur, m.lc_desc_leg)

    # Checkmark ✓ (U+2713)
    glyphs[0x2713] = _build_checkmark(m)
    return glyphs


def _patch_existing_cmap_subtables(cmap_table: object, new_mappings: dict[int, str]) -> None:
    for st in cmap_table.tables:
        if st.format in (4, 12):
            for cp, gname in new_mappings.items():
                st.cmap.setdefault(cp, gname)


def _ensure_cmap_format4_and_12(tt: TTFont, new_mappings: dict[int, str]) -> None:
    cmap_table = tt["cmap"]
    base_bmp = dict(tt.getBestCmap() or {})
    for cp, gname in new_mappings.items():
        base_bmp.setdefault(cp, gname)

    _patch_existing_cmap_subtables(cmap_table, new_mappings)
    existing_keys = {(st.format, st.platformID, st.platEncID) for st in cmap_table.tables}
    for plat_id, enc_id in ((0, 4), (3, 10)):
        if (12, plat_id, enc_id) not in existing_keys:
            st12 = CmapSubtable.newSubtable(12)
            st12.platformID = plat_id
            st12.platEncID = enc_id
            st12.language = 0
            st12.cmap = dict(base_bmp)
            cmap_table.tables.append(st12)


def _inject_glyphs_surgically(tt: TTFont, additions: dict[int, tuple[Glyph, int]]) -> None:
    existing_cmap = tt.getBestCmap() or {}
    glyph_order = list(tt.getGlyphOrder())
    glyf = tt["glyf"]
    hmtx = tt["hmtx"].metrics

    cmap_updates: dict[int, str] = {}
    for cp in sorted(additions):
        if cp in existing_cmap:
            continue
        gname = f"uni{cp:04X}"
        glyph, adv = additions[cp]
        glyph.recalcBounds(glyf)
        lsb = int(getattr(glyph, "xMin", 0))
        glyph_order.append(gname)
        glyf[gname] = glyph
        hmtx[gname] = (int(adv), lsb)
        cmap_updates[cp] = gname

    tt.setGlyphOrder(glyph_order)
    _ensure_cmap_format4_and_12(tt, cmap_updates)
    tt["OS/2"].ulUnicodeRange1 |= 1 << 9
    head = tt["head"]
    orig_bbox = (head.xMin, head.yMin, head.xMax, head.yMax)
    tt["maxp"].recalc(tt)
    head.xMin, head.yMin, head.xMax, head.yMax = orig_bbox


def _graft_scaled_glyph(
    inter_gset: object, inter_glyf: object, sora_glyf: object, src_gname: str, scale: float
) -> Glyph:
    rec = DecomposingRecordingPen(inter_gset)
    inter_gset[src_gname].draw(rec)
    tt_pen = TTGlyphPen(None)
    t_pen = TransformPen(tt_pen, (scale, 0.0, 0.0, scale, 0.0, 0.0))
    rec.replay(t_pen)
    raw_glyph = tt_pen.glyph()
    pts, end_pts, flags = raw_glyph.getCoordinates(inter_glyf)
    contours: list[Contour] = []
    start = 0
    for ep in end_pts:
        contours.append(
            [(int(round(pts[i][0])), int(round(pts[i][1])), int(flags[i] & 1)) for i in range(start, ep + 1)]
        )
        start = ep + 1
    return _glyph_from_contours(sora_glyf, contours)


def build_sora_font(src_ttf: Path, inter_ttf: Path, out_ttf: Path) -> Path:
    _require_file(src_ttf)
    _require_file(inter_ttf)

    sora = TTFont(src_ttf, recalcBBoxes=False, recalcTimestamp=False)
    inter = TTFont(inter_ttf)
    scale = sora["OS/2"].sCapHeight / inter["OS/2"].sCapHeight

    sora_cmap = sora.getBestCmap() or {}
    inter_cmap = inter.getBestCmap() or {}
    inter_gset = inter.getGlyphSet()
    inter_hmtx = inter["hmtx"].metrics
    sora_glyf = sora["glyf"]

    target_cps = [cp for cp in sorted(inter_cmap) if 0x0400 <= cp <= 0x04FF]
    target_cps.extend(cp for cp in (0x00AB, 0x00BB, 0x2116, 0x2713) if cp in inter_cmap)

    additions: dict[int, tuple[Glyph, int]] = {}
    for cp in target_cps:
        if cp in sora_cmap:
            continue
        src_gname = inter_cmap[cp]
        clean_glyph = _graft_scaled_glyph(inter_gset, inter["glyf"], sora_glyf, src_gname, scale)
        adv = int(round(inter_hmtx[src_gname][0] * scale))
        additions[cp] = (clean_glyph, adv)

    _inject_glyphs_surgically(sora, additions)
    out_ttf.parent.mkdir(parents=True, exist_ok=True)
    sora.save(out_ttf, reorderTables=False)
    return out_ttf


def build_display_font(src_ttf: Path, out_ttf: Path) -> Path:
    _require_file(src_ttf)
    tt = TTFont(src_ttf, recalcBBoxes=False, recalcTimestamp=False)
    glyf = tt["glyf"]

    raw_defs = _synthesize_display_glyphs(tt)
    additions = {cp: (_glyph_from_contours(glyf, contours), adv) for cp, (contours, adv) in raw_defs.items()}

    _inject_glyphs_surgically(tt, additions)
    out_ttf.parent.mkdir(parents=True, exist_ok=True)
    tt.save(out_ttf, reorderTables=False)
    return out_ttf


def build_all_fonts(recon_fonts_dir: Path, out_fonts_dir: Path) -> dict[str, Path]:
    if not recon_fonts_dir.is_dir():
        raise FileNotFoundError(f"Missing recon fonts directory: {recon_fonts_dir}")
    out_fonts_dir.mkdir(parents=True, exist_ok=True)

    built: dict[str, Path] = {}
    for fname in DISPLAY_FONTS:
        built[fname] = build_display_font(recon_fonts_dir / fname, out_fonts_dir / fname)
    for sora_name, inter_name in SORA_INTER_PAIRS:
        built[sora_name] = build_sora_font(
            recon_fonts_dir / sora_name, recon_fonts_dir / inter_name, out_fonts_dir / sora_name
        )
    return built


def _draw_pill(
    draw: ImageDraw.ImageDraw,
    box: tuple[int, int, int, int],
    text: str,
    font: ImageFont.FreeTypeFont,
    *,
    active: bool = False,
) -> None:
    fill = (82, 84, 92) if active else (62, 63, 71)
    outline = (230, 228, 223) if active else (92, 94, 102)
    width = 3 if active else 2
    draw.rounded_rectangle(box, radius=12, fill=fill, outline=outline, width=width)
    x0, y0, x1, y1 = box
    cx = (x0 + x1) // 2
    cy = (y0 + y1) // 2
    draw.text((cx, cy), text, fill=(232, 230, 225), font=font, anchor="mm")


def render_specimen_4k(fonts_dir: Path, recon_fonts_dir: Path, out_png: Path) -> Path:
    for fname in (*DISPLAY_FONTS, "Sora-Regular.ttf", "Sora-Bold.ttf"):
        _require_file(fonts_dir / fname)
    label_ttf = _require_file(recon_fonts_dir / "Inter-Regular.ttf")

    width, height = 3840, 2400
    img = Image.new("RGB", (width, height), (35, 33, 28))
    draw = ImageDraw.Draw(img)

    title_font = ImageFont.truetype(str(fonts_dir / "BigShouldersDisplay-Black.ttf"), 64)
    lbl_font = ImageFont.truetype(str(label_ttf), 28)
    draw.text(
        (80, 42),
        "WE MARCH AS ONE  ·  CYRILLIC FOUNDRY SPECIMEN & 4K HUD MOCKUP",
        fill=(232, 230, 225),
        font=title_font,
    )

    # Top section: Side-by-side Live HUD Pill Mockups (EN vs RU)
    hud_eb_48 = ImageFont.truetype(str(fonts_dir / "BigShouldersDisplay-ExtraBold.ttf"), 46)
    hud_eb_36 = ImageFont.truetype(str(fonts_dir / "BigShouldersDisplay-ExtraBold.ttf"), 36)
    hud_blk_52 = ImageFont.truetype(str(fonts_dir / "BigShouldersDisplay-Black.ttf"), 50)
    hud_ant_52 = ImageFont.truetype(str(fonts_dir / "Anton-Regular.ttf"), 48)
    sora_reg_32 = ImageFont.truetype(str(fonts_dir / "Sora-Regular.ttf"), 30)
    sora_bld_32 = ImageFont.truetype(str(fonts_dir / "Sora-Bold.ttf"), 30)

    draw.rounded_rectangle((70, 130, 1890, 560), radius=16, fill=(46, 44, 38), outline=(75, 72, 64), width=2)
    draw.rounded_rectangle((1950, 130, 3770, 560), radius=16, fill=(46, 44, 38), outline=(75, 72, 64), width=2)

    draw.text((110, 152), "ORIGINAL ENGLISH HUD (4K SCALE)", fill=(168, 176, 196), font=lbl_font)
    draw.text((1990, 152), "RUSSIAN LOCALIZED HUD (4K SCALE)", fill=(168, 176, 196), font=lbl_font)

    # EN Pills
    _draw_pill(draw, (110, 210, 350, 294), "LEVEL 5", hud_eb_48)
    _draw_pill(draw, (380, 210, 570, 294), "MENU", hud_eb_48)
    _draw_pill(draw, (610, 210, 710, 294), "2D", hud_eb_36)
    _draw_pill(draw, (726, 210, 826, 294), "3D", hud_eb_36, active=True)
    _draw_pill(draw, (842, 210, 956, 294), "FLY", hud_eb_36)
    _draw_pill(draw, (990, 210, 1380, 294), "GAME SPEED  1×", hud_eb_36)
    _draw_pill(draw, (1410, 210, 1850, 294), "ACHIEVEMENTS · 6 / 30", hud_eb_36)

    draw.text((110, 326), "DECISIVE · HEROIC  |  VICTORY / DEFEAT", fill=(232, 230, 225), font=hud_blk_52)
    draw.text((1040, 326), "CAMPAIGN · TACTICS · SANDBOX", fill=(232, 230, 225), font=hud_ant_52)
    draw.text(
        (110, 412),
        "Pikeman  ·  Cheap soldier that fights up close.  120 health · 18 damage every 1.5s",
        fill=(214, 212, 206),
        font=sora_reg_32,
    )
    draw.text(
        (110, 468),
        "STRONG VS: Horsemen & Lancers   ✓ Mastered   «We March as One»   #1",
        fill=(232, 230, 225),
        font=sora_bld_32,
    )

    # RU Pills
    _draw_pill(draw, (1990, 210, 2260, 294), "УРОВЕНЬ 5", hud_eb_48)
    _draw_pill(draw, (2286, 210, 2476, 294), "МЕНЮ", hud_eb_48)
    _draw_pill(draw, (2506, 210, 2606, 294), "2D", hud_eb_36)
    _draw_pill(draw, (2622, 210, 2722, 294), "3D", hud_eb_36, active=True)
    _draw_pill(draw, (2738, 210, 2866, 294), "ПОЛЁТ", hud_eb_36)
    _draw_pill(draw, (2894, 210, 3300, 294), "СКОРОСТЬ ИГРЫ  1×", hud_eb_36)
    _draw_pill(draw, (3324, 210, 3740, 294), "ДОСТИЖЕНИЯ · 6 / 30", hud_eb_36)

    draw.text((1990, 326), "РЕШИТЕЛЬНАЯ · ГЕРОИЧЕСКАЯ  |  ПОБЕДА", fill=(232, 230, 225), font=hud_blk_52)
    draw.text((2930, 326), "КАМПАНИЯ · ТАКТИКА · СБОР", fill=(232, 230, 225), font=hud_ant_52)
    draw.text(
        (1990, 412),
        "Пикинёр  ·  Недорогой боец ближнего боя.  120 ОЗ · 18 урона каждые 1.5 с",
        fill=(214, 212, 206),
        font=sora_reg_32,
    )
    draw.text(
        (1990, 468),
        "СИЛЁН ПРОТИВ: Всадники и уланы   ✓ Освоено   «Мы идём как один»   №1",
        fill=(232, 230, 225),
        font=sora_bld_32,
    )

    # Full Alphabet & Symbols Specimen Rows across all 5 built fonts
    spec_specs = [
        ("BigShouldersDisplay-ExtraBold.ttf", 72, "Big Shoulders Display ExtraBold (#478)"),
        ("BigShouldersDisplay-Black.ttf", 72, "Big Shoulders Display Black (#482)"),
        ("Anton-Regular.ttf", 66, "Anton Regular (#477)"),
        ("Sora-Regular.ttf", 52, "Sora Regular (#476)"),
        ("Sora-Bold.ttf", 52, "Sora Bold (#479)"),
    ]

    y_cur = 605
    for fname, pt_size, title in spec_specs:
        draw.rounded_rectangle(
            (70, y_cur, 3770, y_cur + 332), radius=14, fill=(44, 45, 51), outline=(72, 74, 82), width=2
        )
        draw.text((105, y_cur + 18), title, fill=(145, 185, 255), font=lbl_font)
        fnt = ImageFont.truetype(str(fonts_dir / fname), pt_size)
        draw.text(
            (105, y_cur + 64),
            "А Б В Г Д Е Ё Ж З И Й К Л М Н О П Р С Т У Ф Х Ц Ч Ш Щ Ъ Ы Ь Э Ю Я   №1  ✓  «1× · 2D · 3D»",
            fill=(236, 234, 229),
            font=fnt,
        )
        draw.text(
            (105, y_cur + 156),
            "а б в г д е ё ж з и й к л м н о п р с т у ф х ц ч ш щ ъ ы ь э ю я   0123456789 — •",
            fill=(216, 214, 208),
            font=fnt,
        )
        draw.text(
            (105, y_cur + 244),
            "УРОВЕНЬ 5 · МЕНЮ · ПОЛЁТ · ДРЕВО НАВЫКОВ · ВЗАИМНОЕ УНИЧТОЖЕНИЕ · Мушкетёры и гептагоны",
            fill=(198, 224, 188),
            font=fnt,
        )
        y_cur += 352

    out_png.parent.mkdir(parents=True, exist_ok=True)
    img.save(out_png)
    return out_png


def main(argv: list[str] | None = None) -> int:
    root_default = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description="Build Cyrillic fonts and 4K specimen for We March as One.")
    parser.add_argument("--recon-fonts", type=Path, default=root_default / "recon" / "fonts")
    parser.add_argument("--out-fonts", type=Path, default=root_default / "build" / "fonts")
    parser.add_argument("--specimen", type=Path, default=root_default / "diag" / "font_specimen_4k.png")
    args = parser.parse_args(argv)

    build_all_fonts(args.recon_fonts, args.out_fonts)
    render_specimen_4k(args.out_fonts, args.recon_fonts, args.specimen)
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
