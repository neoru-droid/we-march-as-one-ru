#!/bin/sh
"exec" "python3" "$0" "$@"
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol, TextIO

TARGET_APP_ID = 5189160
GAME_FOLDER_NAME = "We March as One"
GAME_EXE_NAME = "WeMarchAsOne.exe"
SCHEMA_FILE_NAME = "UserGameStatsSchema_5189160.bin"
SETUP_EXE_NAME = "WeMarchAsOne-RU-Setup.exe"
BACKUP_DIR_NAME = "ru_backup_orig"
MANIFEST_FILE_NAME = "backup_manifest.json"
ENGLISH_BUNDLE_NAME = "localization-string-tables-english_assets_all.bundle"

REL_RESOURCES = r"WeMarchAsOne_Data\resources.assets"
REL_BUNDLE = (
    r"WeMarchAsOne_Data\StreamingAssets\aa\StandaloneWindows64"
    r"\localization-string-tables-english_assets_all.bundle"
)
REL_CATALOG = r"WeMarchAsOne_Data\StreamingAssets\aa\catalog.bin"
REL_ASSEMBLY = r"WeMarchAsOne_Data\Managed\Assembly-CSharp.dll"
REL_STEAM_SCHEMA = r"steam_stats\UserGameStatsSchema_5189160.bin"

TRACKED_GAME_KEYS: tuple[str, ...] = (
    REL_RESOURCES,
    REL_BUNDLE,
    REL_CATALOG,
    REL_ASSEMBLY,
)

FONT_SPECS: tuple[tuple[str, str], ...] = (
    ("Sora-Regular", "Sora-Regular.ttf"),
    ("Anton-Regular", "Anton-Regular.ttf"),
    ("BigShouldersDisplay-ExtraBold", "BigShouldersDisplay-ExtraBold.ttf"),
    ("Sora-Bold", "Sora-Bold.ttf"),
    ("BigShouldersDisplay-Black", "BigShouldersDisplay-Black.ttf"),
)
FONT_FILE_NAMES: tuple[str, ...] = tuple(fname for _, fname in FONT_SPECS)
TTF_MAGICS: tuple[bytes, ...] = (b"\x00\x01\x00\x00", b"OTTO", b"true")

MENUS_LOCKED_ENTRY_ID = 1282142286594048
CATALOG_MAGIC = 0x0DE38942
CLASS_ID_MONOBEHAVIOUR = 114
CLASS_ID_FONT = 128

LZ4_CHUNK_SIZE = 0x20000
LZ4_MIN_MATCH = 4
LZ4_LAST_LITERALS = 5
LZ4_MF_LIMIT = 12
LZ4_MAX_DISTANCE = 65535
LZ4_HASH_LOG = 12
LZ4_HASH_SIZE = 1 << LZ4_HASH_LOG
SHIFT_BLOCK_SIZE = 128

VDF_LIB_REGEX = re.compile(r'"path"\s+"([^"]+)"')
ACF_DIR_REGEX = re.compile(r'"installdir"\s+"([^"]+)"')
USER_STATS_REGEX = re.compile(r"^UserGameStats_(\d+)_5189160\.bin$", re.IGNORECASE)

KV_MAP = 0x00
KV_STR = 0x01
KV_INT32 = 0x02
KV_WSTR = 0x05
KV_END = 0x08
KV_SCALAR_SIZES: dict[int, int] = {0x02: 4, 0x03: 4, 0x04: 4, 0x06: 4, 0x07: 8, 0x0A: 8}


@dataclass(frozen=True)
class AchievementDef:
    bit: int
    ach_id: str
    name_ru: str
    desc_ru: str


@dataclass(frozen=True)
class PayloadBundle:
    version: str
    app_id: int
    tables: dict[str, dict[str, str]]
    steam_achievements: dict[str, dict[str, str]]
    achievements: tuple[AchievementDef, ...]
    fonts: dict[str, bytes]
    tools: dict[str, bytes]
    asm_xor_patches: dict[str, dict[str, str]]


@dataclass
class KvNode:
    tag: int
    raw_key: bytes
    children: list[KvNode] = field(default_factory=list)
    raw_val: bytes = b""

    @property
    def key(self) -> str:
        return self.raw_key.decode("utf-8", errors="replace")


@dataclass
class ProfileProgress:
    path: Path
    mtime: float
    bitmask: int
    unlock_times: dict[int, int]
    unlocked_count: int

    def is_unlocked(self, bit: int) -> bool:
        return 0 <= bit < 32 and bool((self.bitmask >> bit) & 1)

    def format_status(self, bit: int, *, tz: Any = None) -> str:
        if not self.is_unlocked(bit):
            return "Не получено"
        ts = self.unlock_times.get(bit, 0)
        if ts > 0:
            dt = datetime.fromtimestamp(ts, tz=timezone.utc).astimezone(tz)
            return f"Открыто {dt.strftime('%d.%m.%Y в %H:%M')}"
        return "Открыто в Steam"


@dataclass
class ManifestEntry:
    existed: bool = True
    orig_sha256: str = ""
    patched_sha256: str = ""
    live_path: str | None = None


@dataclass
class BackupManifest:
    app_id: int = TARGET_APP_ID
    schema_version: int = 2
    achievements_ui: bool = True
    updated_utc: str = ""
    entries: dict[str, ManifestEntry] = field(default_factory=dict)


class GuiBackend(Protocol):
    def choose_action(
        self, title: str, status_line: str, items: list[tuple[str, str]]
    ) -> str | None: ...

    def pick_directory(self, title: str, initial_dir: Path | None) -> Path | None: ...

    def show_info(self, title: str, message: str) -> None: ...

    def show_error(self, title: str, message: str) -> None: ...

    def show_report(self, title: str, body: str) -> None: ...


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def win_rel_to_subpath(root: Path, win_rel: str) -> Path:
    parts = [p for p in win_rel.replace("\\", "/").split("/") if p]
    return root.joinpath(*parts)


def write_bytes_atomic(target: Path, data: bytes) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = target.parent / f".{target.name}.wmao_tmp"
    try:
        tmp_path.write_bytes(data)
        os.replace(tmp_path, target)
    finally:
        tmp_path.unlink(missing_ok=True)


def _decode_b64_map(raw_map: dict[str, Any]) -> dict[str, bytes]:
    return {str(k): base64.b64decode(str(v)) for k, v in raw_map.items()}


def _parse_ach_list(
    raw_list: list[dict[str, Any]], steam_ach: dict[str, dict[str, str]]
) -> tuple[AchievementDef, ...]:
    if raw_list:
        defs = [
            AchievementDef(
                bit=int(item["bit"]),
                ach_id=str(item.get("id", "")),
                name_ru=str(item["name_ru"]),
                desc_ru=str(item["desc_ru"]),
            )
            for item in raw_list
        ]
        defs.sort(key=lambda a: a.bit)
        return tuple(defs)
    return tuple(
        AchievementDef(idx, api_id, str(tr["name_ru"]), str(tr["desc_ru"]))
        for idx, (api_id, tr) in enumerate(steam_ach.items())
    )


def _bundle_from_json_dict(root: dict[str, Any]) -> PayloadBundle | None:
    if root.get("app_id") != TARGET_APP_ID:
        return None
    if not {"tables", "steam_achievements", "fonts"}.issubset(root):
        return None
    tables = {
        str(col): {str(rk): str(rv) for rk, rv in dict(rows).items()}
        for col, rows in dict(root["tables"]).items()
    }
    steam_ach = {
        str(k): {"name_ru": str(v["name_ru"]), "desc_ru": str(v["desc_ru"])}
        for k, v in dict(root["steam_achievements"]).items()
    }
    raw_xor = dict(root.get("asm_xor_patches", {}))
    xor_map = {str(h): {str(mk): str(mv) for mk, mv in dict(m).items()} for h, m in raw_xor.items()}
    return PayloadBundle(
        version=str(root.get("version", "1.1.0")),
        app_id=TARGET_APP_ID,
        tables=tables,
        steam_achievements=steam_ach,
        achievements=_parse_ach_list(list(root.get("achievements", [])), steam_ach),
        fonts=_decode_b64_map(dict(root["fonts"])),
        tools=_decode_b64_map(dict(root.get("tools", {}))),
        asm_xor_patches=xor_map,
    )


def _decompress_gzip_member(raw: bytes, offset: int) -> PayloadBundle | None:
    try:
        dec = zlib.decompressobj(16 + zlib.MAX_WBITS)
        body = dec.decompress(raw[offset:]) + dec.flush()
        doc = json.loads(body.decode("utf-8")) if dec.eof else None
    except Exception:
        return None
    return _bundle_from_json_dict(doc) if isinstance(doc, dict) else None


def extract_payload_from_bytes(raw: bytes) -> PayloadBundle:
    cursor = 0
    while True:
        hit = raw.find(b"\x1f\x8b\x08", cursor)
        if hit < 0:
            break
        parsed = _decompress_gzip_member(raw, hit)
        if parsed is not None:
            return parsed
        cursor = hit + 1
    raise ValueError("Не удалось найти встроенный ресурс русификатора (wmao_ru_payload.gz).")


def _candidate_payload_paths(base_dir: Path) -> tuple[Path, ...]:
    return (
        base_dir / SETUP_EXE_NAME,
        base_dir / "wmao_ru_payload.gz",
        base_dir.parent / "build" / "wmao_ru_payload.gz",
    )


def load_payload(explicit_payload: Path | None, script_path: Path) -> PayloadBundle:
    if explicit_payload is not None:
        if not explicit_payload.is_file():
            raise ValueError(f"Не найден файл ресурсов русификатора: {explicit_payload}")
        return extract_payload_from_bytes(explicit_payload.read_bytes())

    base_dir = script_path if script_path.is_dir() else script_path.resolve().parent
    for cand in _candidate_payload_paths(base_dir):
        if cand.is_file():
            return extract_payload_from_bytes(cand.read_bytes())
    raise ValueError(
        f"Не найден {SETUP_EXE_NAME} рядом со скриптом. Распакуйте весь архив в одну папку."
    )


def is_valid_game_dir(path: Path | None) -> bool:
    if path is None:
        return False
    res_ok = (path / "WeMarchAsOne_Data" / "resources.assets").is_file()
    cat_ok = (path / "WeMarchAsOne_Data" / "StreamingAssets" / "aa" / "catalog.bin").is_file()
    return res_ok and cat_ok


def _clean_user_path(raw: Path | str | None) -> Path | None:
    if raw is None:
        return None
    txt = str(raw).strip().strip('"')
    return Path(txt).expanduser() if txt else None


def normalize_game_dir(picked: Path | str | None) -> Path | None:
    cand = _clean_user_path(picked)
    if cand is None:
        return None
    if cand.is_file():
        cand = cand.parent
    if is_valid_game_dir(cand):
        return cand.resolve()
    if is_valid_game_dir(cand.parent):
        return cand.parent.resolve()
    return cand


def _steam_home_roots(home: Path) -> tuple[Path, ...]:
    return (
        home / ".local" / "share" / "Steam",
        home / ".steam" / "steam",
        home / ".steam" / "root",
        home / ".var" / "app" / "com.valvesoftware.Steam" / ".local" / "share" / "Steam",
    )


def _looks_like_steam_root(cand: Path) -> bool:
    if not cand.is_dir():
        return False
    return (
        (cand / "appcache" / "stats").is_dir()
        or (cand / "steamapps" / "libraryfolders.vdf").is_file()
        or (cand / "steam.sh").is_file()
    )


def _steam_root_from_game(game_dir: Path | str | None) -> Path | None:
    norm = normalize_game_dir(game_dir)
    if norm is None:
        return None
    if norm.parent.name.lower() == "common" and norm.parent.parent.name.lower() == "steamapps":
        return norm.parent.parent.parent
    return None


def detect_steam_dir(
    explicit_steam: Path | str | None,
    hint_game: Path | str | None = None,
    *,
    home_dir: Path | None = None,
) -> Path | None:
    cleaned = _clean_user_path(explicit_steam)
    if cleaned is not None:
        return cleaned.resolve() if cleaned.is_dir() else None
    for cand in _steam_home_roots(home_dir or Path.home()):
        if _looks_like_steam_root(cand):
            return cand.resolve()
    inferred = _steam_root_from_game(hint_game)
    return inferred.resolve() if (inferred is not None and _looks_like_steam_root(inferred)) else None


def resolve_stats_dir(steam_dir: Path | None) -> Path | None:
    if steam_dir is None:
        return None
    nested = steam_dir / "appcache" / "stats"
    if nested.is_dir():
        return nested
    if (steam_dir / SCHEMA_FILE_NAME).is_file() or steam_dir.name == "stats":
        return steam_dir
    return nested


def _parse_vdf_libraries(steam_root: Path) -> list[Path]:
    vdf = steam_root / "steamapps" / "libraryfolders.vdf"
    if not vdf.is_file():
        return []
    raw_txt = vdf.read_text(encoding="utf-8", errors="ignore")
    return [Path(m.replace("\\\\", "\\")) for m in VDF_LIB_REGEX.findall(raw_txt)]


def _removable_media_roots(media_root: Path, user_name: str) -> list[Path]:
    roots: list[Path] = [media_root / "mmcblk0p1", media_root / "mmcblk0p1" / "SteamLibrary"]
    for owner in dict.fromkeys(("deck", user_name)):
        owner_path = (media_root / owner) if owner else None
        if owner_path is not None and owner_path.is_dir():
            for mount in sorted(owner_path.iterdir()):
                roots.extend((mount, mount / "SteamLibrary"))
    return roots


def _library_game_dirs(lib_root: Path) -> list[Path]:
    steamapps = lib_root / "steamapps"
    acf = steamapps / f"appmanifest_{TARGET_APP_ID}.acf"
    found: list[Path] = []
    if acf.is_file():
        hit = ACF_DIR_REGEX.search(acf.read_text(encoding="utf-8", errors="ignore"))
        if hit:
            found.append(steamapps / "common" / hit.group(1))
    found.append(steamapps / "common" / GAME_FOLDER_NAME)
    return found


def _collect_steam_libraries(
    steam_dir: Path | None, home: Path, media_root: Path, user: str
) -> list[Path]:
    libs: list[Path] = []
    for root in (steam_dir, *_steam_home_roots(home)):
        if root is not None and root.is_dir():
            libs.append(root)
            libs.extend(_parse_vdf_libraries(root))
    libs.extend(_removable_media_roots(media_root, user))
    return libs


def detect_game_dir(
    explicit_game: Path | str | None,
    steam_dir: Path | None,
    script_dir: Path,
    *,
    home_dir: Path | None = None,
    media_root: Path = Path("/run/media"),
    user_name: str | None = None,
) -> Path | None:
    if _clean_user_path(explicit_game) is not None:
        norm = normalize_game_dir(explicit_game)
        return norm if is_valid_game_dir(norm) else None

    user = user_name if user_name is not None else os.environ.get("USER", "")
    libs = _collect_steam_libraries(steam_dir, home_dir or Path.home(), media_root, user)
    cands = [g for lib in libs for g in _library_game_dirs(lib)]
    cands.extend((script_dir, script_dir.parent))
    return next((cand.resolve() for cand in cands if is_valid_game_dir(cand)), None)


def _pid_matches_wmao(pid_dir: Path) -> bool:
    if not pid_dir.name.isdigit():
        return False
    try:
        return b"wemarchasone.exe" in (pid_dir / "cmdline").read_bytes().lower()
    except OSError:
        return False


def assert_game_closed(proc_root: Path = Path("/proc")) -> None:
    if not proc_root.is_dir():
        return
    for item in proc_root.iterdir():
        if _pid_matches_wmao(item):
            raise RuntimeError(
                f"Игра We March as One сейчас запущена (PID {item.name}). "
                "Закройте игру перед установкой или возвратом оригинала."
            )


def _read_cstring_utf8(buf: bytes, pos: int) -> tuple[str, int]:
    end = buf.find(0, pos)
    if end < 0:
        raise ValueError(f"Незакрытая C-строка на смещении {pos}.")
    return buf[pos:end].decode("utf-8"), end + 1


def _read_aligned_utf8(buf: bytes, pos: int) -> tuple[str, int]:
    if pos + 4 > len(buf):
        raise ValueError(f"Обрыв длины строки на смещении {pos}.")
    str_len = struct.unpack_from("<i", buf, pos)[0]
    pos += 4
    if str_len < 0 or pos + str_len > len(buf):
        raise ValueError(f"Некорректная длина строки {str_len} на смещении {pos - 4}.")
    text = buf[pos : pos + str_len].decode("utf-8")
    return text, (pos + str_len + 3) & ~3


def _encode_aligned_utf8(text: str) -> bytes:
    raw = text.encode("utf-8")
    pad = (4 - (len(raw) & 3)) & 3
    return struct.pack("<i", len(raw)) + raw + (b"\x00" * pad)


def _skip_serialized_type(buf: bytes, pos: int, enable_tt: bool, data_offset: int) -> tuple[int, int]:
    class_id = struct.unpack_from("<i", buf, pos)[0]
    pos += 7 + (16 if class_id == CLASS_ID_MONOBEHAVIOUR else 0) + 16
    if not enable_tt:
        return class_id, pos
    pos += 16
    tt_size = struct.unpack_from("<i", buf, pos)[0]
    pos += 4 + tt_size
    dep_cnt = struct.unpack_from("<i", buf, pos)[0]
    pos += 4 + dep_cnt * 4
    if tt_size < 0 or dep_cnt < 0 or pos > data_offset:
        raise ValueError("Повреждены метаданные TypeTree в SerializedFile v23.")
    return class_id, pos


def _validate_v23_header(raw: bytes) -> tuple[int, int]:
    if len(raw) < 48 or struct.unpack_from(">I", raw, 8)[0] != 23 or raw[16] != 0:
        raise ValueError("Ожидался little-endian Unity SerializedFile версии 23.")
    meta_size, orig_size, data_off = struct.unpack_from(">IQQ", raw, 20)
    if orig_size != len(raw) or data_off < 48 + meta_size or data_off > len(raw):
        raise ValueError("Некорректные смещения заголовка SerializedFile v23.")
    return meta_size, data_off


def _parse_serialized_v23_meta(raw: bytes) -> tuple[int, list[int], int, int]:
    meta_size, data_off = _validate_v23_header(raw)
    _, pos = _read_cstring_utf8(raw, 48)
    enable_tt = raw[pos + 4] != 0
    type_cnt = struct.unpack_from("<i", raw, pos + 5)[0]
    pos += 9
    class_ids: list[int] = []
    for _ in range(type_cnt):
        cid, pos = _skip_serialized_type(raw, pos, enable_tt, data_off)
        class_ids.append(cid)
    obj_cnt = struct.unpack_from("<i", raw, pos)[0]
    obj_table_off = (pos + 4 + 3) & ~3
    if obj_cnt < 0 or obj_table_off + obj_cnt * 24 > 48 + meta_size:
        raise ValueError("Таблица объектов SerializedFile v23 выходит за границы.")
    return data_off, class_ids, obj_cnt, obj_table_off


def rebuild_serialized_v23(
    raw: bytes, mutator: Callable[[int, int, bytes], bytes]
) -> bytes:
    data_off, class_ids, obj_cnt, table_off = _parse_serialized_v23_meta(raw)
    header_meta = bytearray(raw[:data_off])
    data_out = bytearray()

    for idx in range(obj_cnt):
        entry_off = table_off + idx * 24
        path_id, rel_start, byte_size, type_id = struct.unpack_from("<qqIi", raw, entry_off)
        abs_start = data_off + rel_start
        orig_obj = raw[abs_start : abs_start + byte_size]
        updated = mutator(path_id, class_ids[type_id], orig_obj)
        struct.pack_into("<qI", header_meta, entry_off + 8, len(data_out), len(updated))
        data_out.extend(updated)
        pad = (8 - (len(data_out) & 7)) & 7
        if pad:
            data_out.extend(b"\x00" * pad)

    struct.pack_into(">Q", header_meta, 24, data_off + len(data_out))
    return bytes(header_meta) + bytes(data_out)


def _patch_font_object(
    obj_bytes: bytes, font_by_asset: dict[str, bytes], matched: set[str]
) -> bytes:
    font_name, pos = _read_aligned_utf8(obj_bytes, 0)
    key_lower = font_name.lower()
    if key_lower not in font_by_asset:
        return obj_bytes
    new_ttf = font_by_asset[key_lower]
    pos += 52
    char_rects, kerning_cnt = struct.unpack_from("<ii", obj_bytes, pos)
    if char_rects != 0 or kerning_cnt < 0:
        raise ValueError(f"Неподдерживаемая структура шрифта {font_name!r}.")
    size_off = pos + 8 + kerning_cnt * 8 + 4
    old_size = struct.unpack_from("<i", obj_bytes, size_off)[0]
    old_ttf_off = size_off + 4
    if old_size < 12 or obj_bytes[old_ttf_off : old_ttf_off + 4] not in TTF_MAGICS:
        raise ValueError(f"Повреждена сигнатура m_FontData в шрифте {font_name!r}.")
    old_aligned_end = (old_ttf_off + old_size + 3) & ~3
    new_pad = (4 - (len(new_ttf) & 3)) & 3
    matched.add(key_lower)
    return (
        obj_bytes[:size_off]
        + struct.pack("<i", len(new_ttf))
        + new_ttf
        + (b"\x00" * new_pad)
        + obj_bytes[old_aligned_end:]
    )


def patch_resources_bytes(raw_assets: bytes, fonts_by_file: dict[str, bytes]) -> tuple[bytes, int]:
    font_by_asset: dict[str, bytes] = {}
    for asset_name, file_name in FONT_SPECS:
        ttf = fonts_by_file.get(file_name)
        if ttf is None or len(ttf) < 12 or ttf[:4] not in TTF_MAGICS:
            raise ValueError(f"Отсутствует или повреждён шрифт {file_name}.")
        font_by_asset[asset_name.lower()] = ttf

    matched: set[str] = set()
    rebuilt = rebuild_serialized_v23(
        raw_assets,
        lambda _pid, cid, data: (
            _patch_font_object(data, font_by_asset, matched) if cid == CLASS_ID_FONT else data
        ),
    )
    if len(matched) != len(FONT_SPECS):
        raise ValueError(f"Обновлено шрифтов {len(matched)} из {len(FONT_SPECS)}.")
    return rebuilt, len(matched)


def _lz4_read_ext_len(src: bytes, ip: int, base: int) -> tuple[int, int]:
    total = base
    while True:
        if ip >= len(src):
            raise ValueError("Обрыв потока LZ4 при чтении расширенной длины.")
        add = src[ip]
        ip += 1
        total += add
        if add != 255:
            return total, ip


def _lz4_copy_match(dst: bytearray, distance: int, match_len: int, max_len: int) -> None:
    src_pos = len(dst) - distance
    if distance <= 0 or src_pos < 0 or len(dst) + match_len > max_len:
        raise ValueError("Некорректное смещение или длина совпадения LZ4.")
    if distance >= match_len:
        dst.extend(dst[src_pos : src_pos + match_len])
        return
    for _ in range(match_len):
        dst.append(dst[src_pos])
        src_pos += 1


def _lz4_step_decompress(src: bytes, ip: int, dst: bytearray, uncomp_len: int) -> int:
    token = src[ip]
    ip += 1
    lit_len = (token >> 4) & 0x0F
    if lit_len == 15:
        lit_len, ip = _lz4_read_ext_len(src, ip, lit_len)
    if ip + lit_len > len(src) or len(dst) + lit_len > uncomp_len:
        raise ValueError("Переполнение литералов LZ4.")
    dst.extend(src[ip : ip + lit_len])
    ip += lit_len
    if ip >= len(src):
        return ip
    if ip + 2 > len(src):
        raise ValueError("Обрыв смещения совпадения LZ4.")
    distance = src[ip] | (src[ip + 1] << 8)
    ip += 2
    match_len = (token & 0x0F) + LZ4_MIN_MATCH
    if (token & 0x0F) == 15:
        match_len, ip = _lz4_read_ext_len(src, ip, match_len)
    _lz4_copy_match(dst, distance, match_len, uncomp_len)
    return ip


def lz4_decompress_block(src: bytes, uncomp_len: int) -> bytes:
    if uncomp_len < 0:
        raise ValueError("Отрицательный размер распаковки LZ4.")
    if uncomp_len == 0:
        return b""
    dst = bytearray()
    ip = 0
    while ip < len(src):
        ip = _lz4_step_decompress(src, ip, dst, uncomp_len)
    if len(dst) != uncomp_len:
        raise ValueError("Несовпадение итогового размера блока LZ4.")
    return bytes(dst)


def _lz4_write_ext(out: bytearray, extra: int) -> None:
    while extra >= 255:
        out.append(255)
        extra -= 255
    out.append(extra)


def _lz4_emit_literals_tail(out: bytearray, tail: bytes) -> None:
    tok = 15 if len(tail) >= 15 else len(tail)
    out.append(tok << 4)
    if len(tail) >= 15:
        _lz4_write_ext(out, len(tail) - 15)
    out.extend(tail)


def _lz4_hash4(buf: bytes, pos: int) -> int:
    val = struct.unpack_from("<I", buf, pos)[0]
    return ((val * 2654435761) & 0xFFFFFFFF) >> (32 - LZ4_HASH_LOG)


def _lz4_emit_sequence(
    out: bytearray, lit: bytes, distance: int, match_len: int
) -> None:
    lit_tok = 15 if len(lit) >= 15 else len(lit)
    m_minus4 = match_len - LZ4_MIN_MATCH
    m_tok = 15 if m_minus4 >= 15 else m_minus4
    out.append((lit_tok << 4) | m_tok)
    if len(lit) >= 15:
        _lz4_write_ext(out, len(lit) - 15)
    out.extend(lit)
    out.append(distance & 0xFF)
    out.append((distance >> 8) & 0xFF)
    if m_minus4 >= 15:
        _lz4_write_ext(out, m_minus4 - 15)


def _lz4_expand_match(
    src: bytes, ip: int, ref: int, anchor: int, match_limit: int
) -> tuple[int, int, int]:
    while ip > anchor and ref > 0 and src[ip - 1] == src[ref - 1]:
        ip -= 1
        ref -= 1
    mlen = LZ4_MIN_MATCH
    while ip + mlen < match_limit and src[ref + mlen] == src[ip + mlen]:
        mlen += 1
    return ip, ref, mlen


def _lz4_is_valid_match(src: bytes, ref: int, ip: int) -> bool:
    return ref >= 0 and (ip - ref) <= LZ4_MAX_DISTANCE and src[ref : ref + 4] == src[ip : ip + 4]


def lz4_compress_block(src: bytes) -> bytes:
    if not src:
        return b""
    out = bytearray()
    if len(src) < LZ4_MF_LIMIT + 1:
        _lz4_emit_literals_tail(out, src)
        return bytes(out)

    htable = [-1] * LZ4_HASH_SIZE
    match_limit = len(src) - LZ4_LAST_LITERALS
    mf_limit = len(src) - LZ4_MF_LIMIT
    anchor = 0
    htable[_lz4_hash4(src, 0)] = 0
    ip = 1

    while ip <= mf_limit:
        h = _lz4_hash4(src, ip)
        ref = htable[h]
        htable[h] = ip
        if not _lz4_is_valid_match(src, ref, ip):
            ip += 1 + ((ip - anchor) >> 6)
            continue
        ip, ref, mlen = _lz4_expand_match(src, ip, ref, anchor, match_limit)
        _lz4_emit_sequence(out, src[anchor:ip], ip - ref, mlen)
        ip += mlen
        anchor = ip
        if ip <= mf_limit:
            htable[_lz4_hash4(src, ip - 2)] = ip - 2

    _lz4_emit_literals_tail(out, src[anchor:])
    return bytes(out)


def _decompress_unityfs_chunk(chunk: bytes, uncomp_len: int, mode: int) -> bytes:
    if mode == 0:
        if len(chunk) != uncomp_len:
            raise ValueError("Несовпадение размера несжатого блока UnityFS.")
        return chunk
    if mode in (2, 3):
        return lz4_decompress_block(chunk, uncomp_len)
    raise ValueError(f"Неподдерживаемый режим сжатия UnityFS: {mode}")


def _parse_unityfs_blocks_info(
    bi_bytes: bytes,
) -> tuple[list[tuple[int, int, int]], list[tuple[int, int, int, str]]]:
    pos = 16
    blk_cnt = struct.unpack_from(">i", bi_bytes, pos)[0]
    pos += 4
    blocks: list[tuple[int, int, int]] = []
    for _ in range(blk_cnt):
        u_sz, c_sz, flags = struct.unpack_from(">IIH", bi_bytes, pos)
        pos += 10
        blocks.append((u_sz, c_sz, flags))
    node_cnt = struct.unpack_from(">i", bi_bytes, pos)[0]
    pos += 4
    nodes: list[tuple[int, int, int, str]] = []
    for _ in range(node_cnt):
        n_off, n_sz, n_fl = struct.unpack_from(">qqI", bi_bytes, pos)
        n_path, pos = _read_cstring_utf8(bi_bytes, pos + 20)
        nodes.append((n_off, n_sz, n_fl, n_path))
    return blocks, nodes


def _unpack_unityfs(raw_bundle: bytes) -> tuple[int, str, str, list[tuple[str, int, bytes]]]:
    if len(raw_bundle) < 32:
        raise ValueError("Файл AssetBundle слишком мал.")
    sig, pos = _read_cstring_utf8(raw_bundle, 0)
    if sig != "UnityFS":
        raise ValueError(f"Неверная сигнатура AssetBundle: {sig!r}")
    fmt_ver = struct.unpack_from(">I", raw_bundle, pos)[0]
    player_ver, pos = _read_cstring_utf8(raw_bundle, pos + 4)
    engine_ver, pos = _read_cstring_utf8(raw_bundle, pos)
    b_size, c_bi, u_bi, a_flags = struct.unpack_from(">qIII", raw_bundle, pos)
    pos += 20
    if b_size != len(raw_bundle):
        raise ValueError("Размер заголовка UnityFS не совпадает с файлом.")
    if fmt_ver >= 7:
        pos = (pos + 15) & ~15

    if a_flags & 0x80:
        comp_bi = raw_bundle[len(raw_bundle) - c_bi :]
    else:
        comp_bi = raw_bundle[pos : pos + c_bi]
        pos += c_bi
    bi_bytes = _decompress_unityfs_chunk(comp_bi, u_bi, a_flags & 0x3F)
    blocks, nodes = _parse_unityfs_blocks_info(bi_bytes)
    if a_flags & 0x200:
        pos = (pos + 15) & ~15

    stream = bytearray()
    for u_sz, c_sz, b_fl in blocks:
        stream.extend(_decompress_unityfs_chunk(raw_bundle[pos : pos + c_sz], u_sz, b_fl & 0x3F))
        pos += c_sz
    node_blobs = [(path, n_fl, bytes(stream[off : off + sz])) for off, sz, n_fl, path in nodes]
    return fmt_ver, player_ver, engine_ver, node_blobs


def _compress_unityfs_stream(uncomp: bytes) -> tuple[list[tuple[int, int, int]], list[bytes]]:
    meta: list[tuple[int, int, int]] = []
    chunks: list[bytes] = []
    for off in range(0, len(uncomp), LZ4_CHUNK_SIZE):
        piece = uncomp[off : off + LZ4_CHUNK_SIZE]
        lz = lz4_compress_block(piece)
        if len(lz) < len(piece):
            meta.append((len(piece), len(lz), 0x0002))
            chunks.append(lz)
        else:
            meta.append((len(piece), len(piece), 0x0000))
            chunks.append(piece)
    return meta, chunks


def pack_unityfs_bundle(
    fmt_ver: int,
    player_ver: str,
    engine_ver: str,
    nodes: Sequence[tuple[str, int, bytes]],
) -> tuple[bytes, int]:
    uncomp = b"".join(data for _, _, data in nodes)
    crc = zlib.crc32(uncomp) & 0xFFFFFFFF
    blk_meta, chunks = _compress_unityfs_stream(uncomp)

    bi = bytearray(16)
    bi.extend(struct.pack(">i", len(blk_meta)))
    for u_sz, c_sz, fl in blk_meta:
        bi.extend(struct.pack(">IIH", u_sz, c_sz, fl))
    bi.extend(struct.pack(">i", len(nodes)))
    cur_off = 0
    for path, n_fl, data in nodes:
        bi.extend(struct.pack(">qqI", cur_off, len(data), n_fl))
        bi.extend(path.encode("utf-8") + b"\x00")
        cur_off += len(data)

    lz_bi = lz4_compress_block(bytes(bi))
    a_flags, final_bi = (0x0242, lz_bi) if len(lz_bi) < len(bi) else (0x0240, bytes(bi))

    out = bytearray(b"UnityFS\x00" + struct.pack(">I", fmt_ver))
    out.extend(player_ver.encode("utf-8") + b"\x00" + engine_ver.encode("utf-8") + b"\x00")
    size_pos = len(out)
    out.extend(struct.pack(">qIII", 0, len(final_bi), len(bi), a_flags))
    if fmt_ver >= 7:
        out.extend(b"\x00" * (((len(out) + 15) & ~15) - len(out)))
    out.extend(final_bi)
    out.extend(b"\x00" * (((len(out) + 15) & ~15) - len(out)))
    for chunk in chunks:
        out.extend(chunk)
    struct.pack_into(">q", out, size_pos, len(out))
    return bytes(out), crc


def _read_table_rows(
    raw: bytes, pos: int, row_cnt: int, tr: dict[str, str]
) -> tuple[list[tuple[int, str, bytes]], set[int], int, int]:
    rows: list[tuple[int, str, bytes]] = []
    seen: set[int] = set()
    updated = 0
    for _ in range(row_cnt):
        entry_id = struct.unpack_from("<q", raw, pos)[0]
        orig_str, pos = _read_aligned_utf8(raw, pos + 8)
        meta_cnt = struct.unpack_from("<i", raw, pos)[0]
        meta_len = 4 + meta_cnt * 8
        meta_blob = raw[pos : pos + meta_len]
        pos += meta_len
        sid = str(entry_id)
        if sid in tr:
            rows.append((entry_id, tr[sid], meta_blob))
            updated += 1
        else:
            rows.append((entry_id, orig_str, meta_blob))
        seen.add(entry_id)
    return rows, seen, pos, updated


def _parse_string_table_header(
    raw: bytes, tables: dict[str, dict[str, str]]
) -> tuple[str, dict[str, str], int, int] | None:
    if len(raw) < 40:
        return None
    obj_name, pos = _read_aligned_utf8(raw, 28)
    tr = tables.get(obj_name[:-3]) if obj_name.endswith("_en") else None
    if tr is None:
        return None
    col = obj_name[:-3]
    _, pos = _read_aligned_utf8(raw, pos)
    t_meta_cnt = struct.unpack_from("<i", raw, pos + 12)[0]
    table_off = pos + 16 + t_meta_cnt * 8
    row_cnt = struct.unpack_from("<i", raw, table_off)[0]
    if row_cnt == 0 and col != "Menus":
        return None
    return col, tr, table_off, row_cnt


def _patch_string_table_mb(
    raw: bytes, tables: dict[str, dict[str, str]]
) -> tuple[bytes, int]:
    hdr = _parse_string_table_header(raw, tables)
    if hdr is None:
        return raw, 0
    col, tr, table_off, row_cnt = hdr
    rows, seen, suffix_off, updated = _read_table_rows(raw, table_off + 4, row_cnt, tr)
    locked_key = str(MENUS_LOCKED_ENTRY_ID)
    if col == "Menus" and MENUS_LOCKED_ENTRY_ID not in seen and locked_key in tr:
        rows.append((MENUS_LOCKED_ENTRY_ID, tr[locked_key], b"\x00\x00\x00\x00"))
        updated += 1

    rebuilt = bytearray(raw[:table_off] + struct.pack("<i", len(rows)))
    for eid, text, mblob in rows:
        rebuilt.extend(struct.pack("<q", eid) + _encode_aligned_utf8(text) + mblob)
    rebuilt.extend(raw[suffix_off:])
    return bytes(rebuilt), updated


def patch_unityfs_bundle_bytes(
    raw_bundle: bytes, tables: dict[str, dict[str, str]]
) -> tuple[bytes, int, int]:
    fmt_ver, player_ver, engine_ver, nodes = _unpack_unityfs(raw_bundle)
    updated_total = 0
    out_nodes: list[tuple[str, int, bytes]] = []
    for n_path, n_fl, n_data in nodes:
        if n_fl & 4:
            def _mut(_pid: int, cid: int, ob: bytes) -> bytes:
                nonlocal updated_total
                if cid != CLASS_ID_MONOBEHAVIOUR:
                    return ob
                patched_ob, cnt = _patch_string_table_mb(ob, tables)
                updated_total += cnt
                return patched_ob

            n_data = rebuild_serialized_v23(n_data, _mut)
        out_nodes.append((n_path, n_fl, n_data))

    if updated_total == 0:
        raise ValueError("В бандле локализации не найдено строк StringTable.")
    packed, crc = pack_unityfs_bundle(fmt_ver, player_ver, engine_ver, out_nodes)
    return packed, updated_total, crc


def _probe_abro_crc_offset(catalog_bytes: bytes, rl_pos: int, hash_off: int) -> int | None:
    data_off = struct.unpack_from("<I", catalog_bytes, rl_pos + 16)[0]
    if data_off + 8 > len(catalog_bytes):
        return None
    pay_off = struct.unpack_from("<I", catalog_bytes, data_off + 4)[0]
    if pay_off + 16 <= len(catalog_bytes) and struct.unpack_from("<I", catalog_bytes, pay_off)[0] == hash_off:
        return pay_off + 8
    return None


def _locate_catalog_crc_offset(catalog_bytes: bytes, bundle_filename: str) -> int:
    if len(catalog_bytes) < 16 or struct.unpack_from("<I", catalog_bytes, 0)[0] != CATALOG_MAGIC:
        raise ValueError("Некорректный заголовок Addressables catalog.bin.")
    id_bytes = (
        "{UnityEngine.AddressableAssets.Addressables.RuntimePath}"
        f"\\StandaloneWindows64\\{bundle_filename}"
    ).encode("utf-8")
    hit = catalog_bytes.find(struct.pack("<I", len(id_bytes)) + id_bytes)
    if hit < 0:
        raise ValueError(f"Бандл {bundle_filename!r} не найден в catalog.bin.")
    int_id_off = hit + 4
    hash_off = int_id_off + len(id_bytes)
    ptr_needle = struct.pack("<I", int_id_off)
    cursor = hash_off
    while True:
        rl = catalog_bytes.find(ptr_needle, cursor)
        if rl < 0 or rl + 20 > len(catalog_bytes):
            break
        found = _probe_abro_crc_offset(catalog_bytes, rl, hash_off)
        if found is not None:
            return found
        cursor = rl + 1
    raise ValueError("Не найдена запись AssetBundleRequestOptions в catalog.bin.")


def patch_catalog_bytes(
    catalog_bytes: bytes, bundle_filename: str, new_crc: int, new_size: int
) -> bytes:
    if new_size <= 0:
        raise ValueError("Размер бандла должен быть положительным.")
    off = _locate_catalog_crc_offset(catalog_bytes, bundle_filename)
    out = bytearray(catalog_bytes)
    struct.pack_into("<II", out, off, new_crc & 0xFFFFFFFF, new_size)
    return bytes(out)


def apply_shifted_xor_patch(src_bytes: bytes, compressed_patch: bytes) -> bytes:
    raw = zlib.decompress(compressed_patch)
    out_len, num_blocks = struct.unpack_from("<II", raw, 0)
    shifts_end = 8 + 4 * num_blocks
    shifts = struct.unpack_from(f"<{num_blocks}i", raw, 8)
    xor_stream = raw[shifts_end:]
    if len(xor_stream) != out_len:
        raise ValueError("Повреждён поток XOR-патча сборки.")

    rebuilt = bytearray(out_len)
    src_len = len(src_bytes)
    for blk_idx, shift in enumerate(shifts):
        off = blk_idx * SHIFT_BLOCK_SIZE
        end = min(out_len, off + SHIFT_BLOCK_SIZE)
        src_base = off - shift
        for k in range(end - off):
            s_idx = src_base + k
            s_byte = src_bytes[s_idx] if 0 <= s_idx < src_len else 0
            rebuilt[off + k] = xor_stream[off + k] ^ s_byte
    return bytes(rebuilt)


def _can_run_mono_patcher(
    mono_bin: str | None, managed_dir: Path | None, tools: dict[str, bytes]
) -> bool:
    req = {"Patcher.exe", "RuModRuntime.dll", "Mono.Cecil.dll", "Mono.Cecil.Rocks.dll"}
    return bool(mono_bin and managed_dir is not None and managed_dir.is_dir() and req.issubset(tools))


def _try_mono_patcher(
    orig_asm: bytes,
    payload: PayloadBundle,
    enable_achievements: bool,
    managed_dir: Path | None,
    which_fn: Callable[[str], str | None] = shutil.which,
    runner: Callable[..., subprocess.CompletedProcess[bytes]] = subprocess.run,
) -> bytes | None:
    mono_bin = which_fn("mono")
    if not _can_run_mono_patcher(mono_bin, managed_dir, payload.tools):
        return None
    with tempfile.TemporaryDirectory() as tmp:
        tdir = Path(tmp)
        for name, blob in payload.tools.items():
            (tdir / name).write_bytes(blob)
        in_dll, out_dll = tdir / "orig.dll", tdir / "patched.dll"
        in_dll.write_bytes(orig_asm)
        extra = [] if enable_achievements else ["--no-achievements"]
        cmd = [str(mono_bin), str(tdir / "Patcher.exe"), "patch", str(in_dll), str(tdir / "RuModRuntime.dll"), str(managed_dir), str(out_dll), *extra]
        res = runner(cmd, capture_output=True, check=False, timeout=30)
        return out_dll.read_bytes() if (res.returncode == 0 and out_dll.is_file()) else None


def patch_assembly_bytes(
    orig_asm: bytes,
    payload: PayloadBundle,
    *,
    enable_achievements: bool = True,
    managed_dir: Path | None = None,
    which_fn: Callable[[str], str | None] = shutil.which,
    runner: Callable[..., subprocess.CompletedProcess[bytes]] = subprocess.run,
) -> bytes:
    orig_sha = sha256_hex(orig_asm)
    entry = payload.asm_xor_patches.get(orig_sha)
    if entry is not None:
        key = "default" if enable_achievements else "noach"
        patched = apply_shifted_xor_patch(orig_asm, base64.b64decode(entry[key]))
        exp_sha = entry.get(f"{key}_sha256", "").lower()
        if exp_sha and sha256_hex(patched) != exp_sha:
            raise ValueError("Несовпадение контрольной суммы SHA-256 после патча Assembly-CSharp.dll.")
        return patched

    via_mono = _try_mono_patcher(
        orig_asm, payload, enable_achievements, managed_dir, which_fn=which_fn, runner=runner
    )
    if via_mono is not None:
        return via_mono
    raise RuntimeError(
        "Версия Assembly-CSharp.dll отличается от базовой, а среда Mono для пересборки не найдена."
    )


def _kv_read_nul(buf: bytes, pos: int) -> tuple[bytes, int]:
    end = buf.find(0, pos)
    if end < 0:
        raise ValueError("Незакрытая UTF-8 строка в бинарном файле Valve KeyValues.")
    return buf[pos:end], end + 1


def _kv_read_wstr(buf: bytes, pos: int) -> tuple[bytes, int]:
    cur = pos
    while cur + 1 < len(buf):
        if buf[cur] == 0 and buf[cur + 1] == 0:
            return buf[pos:cur], cur + 2
        cur += 2
    raise ValueError("Незакрытая UTF-16 строка в Valve KeyValues.")


def _kv_parse_value(buf: bytes, pos: int, tag: int) -> tuple[list[KvNode], bytes, int]:
    if tag == KV_MAP:
        kids, nxt = _kv_parse_map(buf, pos, need_end=True)
        return kids, b"", nxt
    if tag == KV_STR:
        s_raw, nxt = _kv_read_nul(buf, pos)
        return [], s_raw, nxt
    if tag == KV_WSTR:
        w_raw, nxt = _kv_read_wstr(buf, pos)
        return [], w_raw, nxt
    sz = KV_SCALAR_SIZES.get(tag)
    if sz is None or pos + sz > len(buf):
        raise ValueError(f"Неизвестный или оборванный тег VDF {tag:#x} на смещении {pos}.")
    return [], buf[pos : pos + sz], pos + sz


def _kv_parse_map(buf: bytes, pos: int, *, need_end: bool) -> tuple[list[KvNode], int]:
    nodes: list[KvNode] = []
    while pos < len(buf):
        tag = buf[pos]
        pos += 1
        if tag == KV_END:
            return nodes, pos
        rkey, pos = _kv_read_nul(buf, pos)
        kids, rval, pos = _kv_parse_value(buf, pos, tag)
        nodes.append(KvNode(tag=tag, raw_key=rkey, children=kids, raw_val=rval))
    if need_end:
        raise ValueError("Незакрытая карта VDF (отсутствует маркер 0x08).")
    return nodes, pos


def parse_kv_root(raw: bytes) -> tuple[list[KvNode], bool]:
    if not raw:
        raise ValueError("Пустой буфер VDF.")
    nodes, end_pos = _kv_parse_map(raw, 0, need_end=False)
    if end_pos != len(raw):
        raise ValueError(f"Лишние байты после конца VDF на позиции {end_pos}.")
    return nodes, (raw[-1] == KV_END)


def _kv_emit_node(out: bytearray, node: KvNode) -> None:
    out.append(node.tag)
    out.extend(node.raw_key + b"\x00")
    if node.tag == KV_MAP:
        for child in node.children:
            _kv_emit_node(out, child)
        out.append(KV_END)
    elif node.tag == KV_STR:
        out.extend(node.raw_val + b"\x00")
    elif node.tag == KV_WSTR:
        out.extend(node.raw_val + b"\x00\x00")
    else:
        out.extend(node.raw_val)


def serialize_kv_root(nodes: Sequence[KvNode], has_root_end: bool = True) -> bytes:
    out = bytearray()
    for n in nodes:
        _kv_emit_node(out, n)
    if has_root_end:
        out.append(KV_END)
    return bytes(out)


def _kv_find(nodes: Sequence[KvNode], key: str, tag: int) -> KvNode | None:
    low = key.lower()
    for n in nodes:
        if n.tag == tag and n.key.lower() == low:
            return n
    return None


def _kv_upsert_str(nodes: list[KvNode], key: str, value: str) -> None:
    enc = value.encode("utf-8")
    for n in nodes:
        if n.tag == KV_STR and n.key == key:
            n.raw_val = enc
            return
    nodes.append(KvNode(tag=KV_STR, raw_key=key.encode("utf-8"), raw_val=enc))


def _is_achievements_stat_block(node: KvNode) -> bool:
    if node.tag != KV_MAP:
        return False
    tnode = _kv_find(node.children, "type", KV_STR)
    return tnode is not None and tnode.raw_val.decode("utf-8", "ignore") == "ACHIEVEMENTS"


def _find_schema_bits(root_nodes: Sequence[KvNode]) -> list[KvNode]:
    if len(root_nodes) != 1 or root_nodes[0].tag != KV_MAP:
        raise ValueError("Ожидался единственный корневой узел схемы VDF.")
    stats = _kv_find(root_nodes[0].children, "stats", KV_MAP)
    if stats is None:
        raise ValueError("В схеме VDF отсутствует узел stats.")
    for item in stats.children:
        if _is_achievements_stat_block(item):
            bits = _kv_find(item.children, "bits", KV_MAP)
            if bits is not None:
                return bits.children
    raise ValueError("В схеме VDF отсутствует блок ACHIEVEMENTS/bits.")


def _patch_single_schema_bit(
    bit_node: KvNode, ach_map: dict[str, dict[str, str]]
) -> bool:
    if bit_node.tag != KV_MAP:
        return False
    api_node = _kv_find(bit_node.children, "name", KV_STR)
    tr = ach_map.get(api_node.raw_val.decode("utf-8", "ignore")) if api_node else None
    disp = _kv_find(bit_node.children, "display", KV_MAP) if tr else None
    if tr is None or disp is None:
        return False
    n_map = _kv_find(disp.children, "name", KV_MAP)
    d_map = _kv_find(disp.children, "desc", KV_MAP)
    if n_map is None or d_map is None:
        return False
    _kv_upsert_str(n_map.children, "english", tr["name_ru"])
    _kv_upsert_str(n_map.children, "russian", tr["name_ru"])
    _kv_upsert_str(d_map.children, "english", tr["desc_ru"])
    _kv_upsert_str(d_map.children, "russian", tr["desc_ru"])
    return True


def patch_schema_bytes(
    raw_schema: bytes, steam_achievements: dict[str, dict[str, str]]
) -> tuple[bytes, int]:
    nodes, has_end = parse_kv_root(raw_schema)
    bits = _find_schema_bits(nodes)
    count = sum(1 for b in bits if _patch_single_schema_bit(b, steam_achievements))
    return serialize_kv_root(nodes, has_end), count


def _find_stats_group(cache_children: Sequence[KvNode]) -> KvNode | None:
    for key in ("0", "1"):
        cand = _kv_find(cache_children, key, KV_MAP)
        if cand is not None and _kv_find(cand.children, "data", KV_INT32) is not None:
            return cand
    return next(
        (
            c
            for c in cache_children
            if c.tag == KV_MAP and _kv_find(c.children, "AchievementTimes", KV_MAP) is not None
        ),
        None,
    )


def _parse_times_dict(grp: KvNode) -> dict[int, int]:
    tnode = _kv_find(grp.children, "AchievementTimes", KV_MAP)
    if tnode is None:
        return {}
    return {
        int(c.key): struct.unpack("<I", c.raw_val)[0]
        for c in tnode.children
        if c.tag == KV_INT32 and c.key.isdigit()
    }


def _extract_stats_mask_and_times(root_nodes: Sequence[KvNode]) -> tuple[int, dict[int, int]]:
    cache = _kv_find(root_nodes, "cache", KV_MAP)
    if cache is None and len(root_nodes) == 1 and root_nodes[0].tag == KV_MAP:
        cache = root_nodes[0]
    grp = _find_stats_group(cache.children) if cache is not None else None
    if grp is None:
        return 0, {}
    dnode = _kv_find(grp.children, "data", KV_INT32)
    mask = struct.unpack("<I", dnode.raw_val)[0] if dnode is not None else 0
    return mask, _parse_times_dict(grp)


def _parse_user_stats_file(path: Path, total_count: int) -> ProfileProgress | None:
    try:
        nodes, _ = parse_kv_root(path.read_bytes())
        mask, times = _extract_stats_mask_and_times(nodes)
        mtime = path.stat().st_mtime
    except Exception:
        return None
    limit = total_count if total_count > 0 else 30
    unlocked = sum(1 for b in range(limit) if (mask >> b) & 1)
    return ProfileProgress(path, mtime, mask, times, unlocked)


def load_user_profiles(steam_dir: Path | None, total_count: int = 30) -> list[ProfileProgress]:
    stats_dir = resolve_stats_dir(steam_dir)
    if stats_dir is None or not stats_dir.is_dir():
        return []
    files = [p for p in stats_dir.iterdir() if p.is_file() and USER_STATS_REGEX.match(p.name)]
    files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    parsed = [_parse_user_stats_file(p, total_count) for p in files]
    return [p for p in parsed if p is not None]


def load_manifest(game_dir: Path) -> BackupManifest:
    mp = game_dir / BACKUP_DIR_NAME / MANIFEST_FILE_NAME
    if not mp.is_file():
        return BackupManifest()
    doc = json.loads(mp.read_text(encoding="utf-8"))
    entries: dict[str, ManifestEntry] = {}
    for k, item in dict(doc.get("entries", {})).items():
        if isinstance(item, dict):
            entries[str(k)] = ManifestEntry(
                existed=bool(item.get("existed", True)),
                orig_sha256=str(item.get("orig_sha256", "")),
                patched_sha256=str(item.get("patched_sha256", "")),
                live_path=item.get("live_path"),
            )
    return BackupManifest(
        app_id=int(doc.get("app_id", TARGET_APP_ID)),
        schema_version=int(doc.get("schema_version", 2)),
        achievements_ui=bool(doc.get("achievements_ui", True)),
        updated_utc=str(doc.get("updated_utc", "")),
        entries=entries,
    )


def save_manifest(game_dir: Path, manifest: BackupManifest) -> None:
    entries_out: dict[str, dict[str, Any]] = {}
    for k in sorted(manifest.entries):
        ent = manifest.entries[k]
        item: dict[str, Any] = {
            "existed": ent.existed,
            "orig_sha256": ent.orig_sha256,
            "patched_sha256": ent.patched_sha256,
        }
        if ent.live_path:
            item["live_path"] = ent.live_path
        entries_out[k] = item
    doc = {
        "achievements_ui": manifest.achievements_ui,
        "app_id": manifest.app_id,
        "entries": entries_out,
        "schema_version": manifest.schema_version,
        "updated_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    mp = game_dir / BACKUP_DIR_NAME / MANIFEST_FILE_NAME
    write_bytes_atomic(mp, (json.dumps(doc, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))


def _lookup_entry_ci(manifest: BackupManifest, key: str) -> ManifestEntry | None:
    low = key.replace("/", "\\").lower()
    for k, ent in manifest.entries.items():
        if k.replace("/", "\\").lower() == low:
            return ent
    return None


def has_valid_backup(game_dir: Path | None) -> bool:
    if not is_valid_game_dir(game_dir) or game_dir is None:
        return False
    mp = game_dir / BACKUP_DIR_NAME / MANIFEST_FILE_NAME
    if not mp.is_file():
        return False
    try:
        return len(load_manifest(game_dir).entries) > 0
    except Exception:
        return False


def is_russifier_installed(game_dir: Path | None) -> bool:
    if not has_valid_backup(game_dir) or game_dir is None:
        return False
    manifest = load_manifest(game_dir)
    res_ent = _lookup_entry_ci(manifest, REL_RESOURCES)
    if res_ent is None or not res_ent.patched_sha256:
        return False
    live_res = win_rel_to_subpath(game_dir, REL_RESOURCES)
    return live_res.is_file() and sha256_hex(live_res.read_bytes()) == res_ent.patched_sha256.lower()


def _prepare_clean_bytes(
    backup_root: Path,
    manifest: BackupManifest,
    key: str,
    live_path: Path,
    log: Callable[[str], None],
) -> tuple[bytes, str]:
    if not live_path.is_file():
        raise FileNotFoundError(f"Не найден обязательный файл игры: {live_path}")
    bak_path = win_rel_to_subpath(backup_root, key)
    live_bytes = live_path.read_bytes()
    live_sha = sha256_hex(live_bytes)
    prev = _lookup_entry_ci(manifest, key)

    if prev is not None and bak_path.is_file():
        bak_bytes = bak_path.read_bytes()
        bak_sha = sha256_hex(bak_bytes)
        if prev.orig_sha256 and bak_sha != prev.orig_sha256.lower():
            raise ValueError(f"Нарушена целостность резервной копии {key}.")
        if live_sha in (prev.patched_sha256.lower(), bak_sha):
            return bak_bytes, bak_sha
        log(f"  * Обнаружено обновление игры для {key}: резервная копия обновлена.")

    write_bytes_atomic(bak_path, live_bytes)
    return live_bytes, live_sha


def _commit_patched(
    target_path: Path,
    patched_bytes: bytes,
    orig_sha: str,
    manifest: BackupManifest,
    key: str,
    live_override: str | None = None,
) -> None:
    patched_sha = sha256_hex(patched_bytes)
    write_bytes_atomic(target_path, patched_bytes)
    if sha256_hex(target_path.read_bytes()) != patched_sha:
        raise OSError(f"Ошибка проверки SHA-256 после записи {target_path}")
    manifest.entries[key] = ManifestEntry(
        existed=True,
        orig_sha256=orig_sha,
        patched_sha256=patched_sha,
        live_path=live_override,
    )


def _install_schema_optional(
    payload: PayloadBundle,
    steam_dir: Path | None,
    backup_root: Path,
    manifest: BackupManifest,
    log: Callable[[str], None],
) -> None:
    stats_dir = resolve_stats_dir(steam_dir)
    schema_path = (stats_dir / SCHEMA_FILE_NAME) if stats_dir is not None else None
    if schema_path is None or not schema_path.is_file():
        return
    try:
        clean_sch, orig_sha = _prepare_clean_bytes(
            backup_root, manifest, REL_STEAM_SCHEMA, schema_path, log
        )
        patched_sch, cnt = patch_schema_bytes(clean_sch, payload.steam_achievements)
        _commit_patched(
            schema_path, patched_sch, orig_sha, manifest, REL_STEAM_SCHEMA, str(schema_path)
        )
        log(f"  * Обновлена локальная схема достижений Steam ({cnt} шт.).")
    except PermissionError:
        log("  * Примечание: нет прав записи для схемы достижений Steam (файлы игры переведены).")
    except Exception as exc:
        log(f"  * Примечание: схема достижений Steam пропущена ({exc}).")


def install_russifier(
    payload: PayloadBundle,
    game_dir: Path,
    steam_dir: Path | None,
    log: Callable[[str], None],
    *,
    enable_achievements_ui: bool = True,
    skip_assembly: bool = False,
    proc_root: Path = Path("/proc"),
) -> None:
    if not is_valid_game_dir(game_dir):
        raise FileNotFoundError(f"Папка не содержит файлов We March as One: {game_dir}")
    assert_game_closed(proc_root)

    backup_root = game_dir / BACKUP_DIR_NAME
    manifest = load_manifest(game_dir)
    log(f"Папка игры: {game_dir}")
    log("[1/4] Проверка и резервное копирование оригинальных файлов...")

    c_res, sha_res = _prepare_clean_bytes(
        backup_root, manifest, REL_RESOURCES, win_rel_to_subpath(game_dir, REL_RESOURCES), log
    )
    c_bun, sha_bun = _prepare_clean_bytes(
        backup_root, manifest, REL_BUNDLE, win_rel_to_subpath(game_dir, REL_BUNDLE), log
    )
    c_cat, sha_cat = _prepare_clean_bytes(
        backup_root, manifest, REL_CATALOG, win_rel_to_subpath(game_dir, REL_CATALOG), log
    )
    c_asm_pair = (
        None
        if skip_assembly
        else _prepare_clean_bytes(
            backup_root, manifest, REL_ASSEMBLY, win_rel_to_subpath(game_dir, REL_ASSEMBLY), log
        )
    )

    log("[2/4] Встраивание 5 кириллических шрифтов в resources.assets...")
    p_res, font_cnt = patch_resources_bytes(c_res, payload.fonts)
    log(f"  * Обновлено шрифтов: {font_cnt} / 5")

    log("[3/4] Локализация таблиц текста и обновление catalog.bin...")
    p_bun, rows_cnt, crc = patch_unityfs_bundle_bytes(c_bun, payload.tables)
    p_cat = patch_catalog_bytes(c_cat, ENGLISH_BUNDLE_NAME, crc, len(p_bun))
    log(f"  * Переведено строк: {rows_cnt} (CRC32: 0x{crc:08x}, размер: {len(p_bun)} Б)")

    p_asm: bytes | None = None
    if c_asm_pair is not None:
        mode_lbl = (
            "поддержка кириллицы + экран «Достижения»"
            if enable_achievements_ui
            else "поддержка кириллицы (без кнопки «Достижения»)"
        )
        log(f"[4/4] Модификация Assembly-CSharp.dll ({mode_lbl})...")
        managed_dir = game_dir / "WeMarchAsOne_Data" / "Managed"
        p_asm = patch_assembly_bytes(
            c_asm_pair[0], payload, enable_achievements=enable_achievements_ui, managed_dir=managed_dir
        )

    _commit_patched(win_rel_to_subpath(game_dir, REL_RESOURCES), p_res, sha_res, manifest, REL_RESOURCES)
    _commit_patched(win_rel_to_subpath(game_dir, REL_BUNDLE), p_bun, sha_bun, manifest, REL_BUNDLE)
    _commit_patched(win_rel_to_subpath(game_dir, REL_CATALOG), p_cat, sha_cat, manifest, REL_CATALOG)
    if c_asm_pair is not None and p_asm is not None:
        _commit_patched(
            win_rel_to_subpath(game_dir, REL_ASSEMBLY), p_asm, c_asm_pair[1], manifest, REL_ASSEMBLY
        )

    _install_schema_optional(payload, steam_dir, backup_root, manifest, log)
    manifest.achievements_ui = enable_achievements_ui
    save_manifest(game_dir, manifest)
    log("Готово: Русификатор успешно установлен.")


def _resolve_schema_restore_path(steam_dir: Path | None, saved_live: str | None) -> Path | None:
    stats_dir = resolve_stats_dir(steam_dir)
    if stats_dir is not None:
        return stats_dir / SCHEMA_FILE_NAME
    return Path(saved_live) if saved_live else None


def _load_verified_backup_bytes(
    key: str, entry: ManifestEntry, bak_file: Path, is_schema: bool
) -> tuple[bytes, str] | None:
    if not bak_file.is_file():
        if is_schema:
            return None
        raise FileNotFoundError(f"Не найден файл резервной копии: {bak_file}")
    bak_bytes = bak_file.read_bytes()
    bak_sha = sha256_hex(bak_bytes)
    if entry.orig_sha256 and bak_sha != entry.orig_sha256.lower():
        raise ValueError(f"Несовпадение SHA-256 резервной копии {key}.")
    return bak_bytes, bak_sha


def _should_skip_steam_updated(
    live_target: Path, entry: ManifestEntry, bak_sha: str, is_schema: bool
) -> bool:
    if is_schema or not live_target.is_file() or not entry.patched_sha256:
        return False
    cur_sha = sha256_hex(live_target.read_bytes())
    return cur_sha not in (entry.patched_sha256.lower(), bak_sha)


def _write_restored_file(
    live_target: Path, bak_bytes: bytes, is_schema: bool, log: Callable[[str], None]
) -> int:
    try:
        write_bytes_atomic(live_target, bak_bytes)
        return 1
    except PermissionError:
        if is_schema:
            log("  * Примечание: нет прав доступа для отката схемы достижений Steam.")
            return 0
        raise


def _restore_single_entry(
    key: str,
    entry: ManifestEntry,
    game_dir: Path,
    backup_root: Path,
    steam_dir: Path | None,
    log: Callable[[str], None],
) -> int:
    is_schema = key.replace("/", "\\").lower().startswith("steam_stats\\")
    live_target = (
        _resolve_schema_restore_path(steam_dir, entry.live_path)
        if is_schema
        else win_rel_to_subpath(game_dir, key)
    )
    if live_target is None:
        return 0
    if not entry.existed:
        live_target.unlink(missing_ok=True)
        return 0
    verified = _load_verified_backup_bytes(
        key, entry, win_rel_to_subpath(backup_root, key), is_schema
    )
    if verified is None:
        return 0
    bak_bytes, bak_sha = verified
    if _should_skip_steam_updated(live_target, entry, bak_sha, is_schema):
        log(f"  * Пропущен {key} (файл уже обновлён новой версией в Steam).")
        return 0
    return _write_restored_file(live_target, bak_bytes, is_schema, log)


def uninstall_russifier(
    game_dir: Path,
    steam_dir: Path | None,
    log: Callable[[str], None],
    *,
    proc_root: Path = Path("/proc"),
) -> int:
    if not is_valid_game_dir(game_dir):
        raise FileNotFoundError(f"Папка не содержит файлов We March as One: {game_dir}")
    assert_game_closed(proc_root)

    backup_root = game_dir / BACKUP_DIR_NAME
    mp = backup_root / MANIFEST_FILE_NAME
    if not mp.is_file():
        raise RuntimeError(
            "Резервная копия ru_backup_orig не найдена: в папке игры уже оригинальные файлы."
        )
    manifest = load_manifest(game_dir)
    if not manifest.entries:
        raise ValueError("Манифест резервной копии пуст или повреждён.")

    restored = sum(
        _restore_single_entry(k, manifest.entries[k], game_dir, backup_root, steam_dir, log)
        for k in sorted(manifest.entries)
    )
    shutil.rmtree(backup_root, ignore_errors=True)
    log(f"Готово: Восстановлено оригинальных файлов: {restored}.")
    return restored


def _format_ach_pair(
    ach: AchievementDef, active: ProfileProgress | None, tz: Any
) -> tuple[str, str]:
    opened = active is not None and active.is_unlocked(ach.bit)
    mark = "[✓]" if opened else "[ ]"
    status = active.format_status(ach.bit, tz=tz) if active is not None else "Не получено"
    return f"{mark} {ach.name_ru} ({status})", f"    {ach.desc_ru}"


def format_achievements_report(
    payload: PayloadBundle, steam_dir: Path | None, *, tz: Any = None
) -> str:
    profiles = load_user_profiles(steam_dir, len(payload.achievements))
    active = profiles[0] if profiles else None
    unlocked = active.unlocked_count if active is not None else 0
    lines = [
        f"Достижения We March as One: {unlocked} / {len(payload.achievements)}",
        "=" * 56,
    ]
    if active is None:
        lines.append("Профиль статистики в кэше Steam пока не найден (показаны условия всех достижений).")
    lines.append("")
    ordered = sorted(
        payload.achievements,
        key=lambda a: (0 if (active and active.is_unlocked(a.bit)) else 1, a.bit),
    )
    for ach in ordered:
        lines.extend(_format_ach_pair(ach, active, tz))
    return "\n".join(lines) + "\n"


def _read_ach_ui_flag(game_dir: Path | None, valid: bool, has_bak: bool) -> bool:
    if valid and game_dir is not None and has_bak:
        return load_manifest(game_dir).achievements_ui
    return True


def format_status_report(
    payload: PayloadBundle, game_dir: Path | None, steam_dir: Path | None
) -> tuple[str, int]:
    valid = is_valid_game_dir(game_dir)
    profiles = load_user_profiles(steam_dir, len(payload.achievements))
    unlocked = profiles[0].unlocked_count if profiles else 0
    has_bak = has_valid_backup(game_dir)
    ach_ui = _read_ach_ui_flag(game_dir, valid, has_bak)
    lines = [
        f"Version: {payload.version}",
        f"GameDir: {game_dir if valid else 'NOT_FOUND'}",
        f"SteamDir: {steam_dir if steam_dir is not None else 'NOT_FOUND'}",
        f"Installed: {'true' if is_russifier_installed(game_dir) else 'false'}",
        f"AchievementsUi: {'true' if ach_ui else 'false'}",
        f"BackupManifest: {'present' if has_bak else 'absent'}",
        f"AchievementsUnlocked: {unlocked}/{len(payload.achievements)}",
    ]
    return "\n".join(lines) + "\n", (0 if valid else 2)


def _run_stdout_cmd(
    runner: Callable[..., subprocess.CompletedProcess[str]], cmd: list[str]
) -> str:
    res = runner(cmd, capture_output=True, text=True, check=False)
    return res.stdout.strip() if res.returncode == 0 else ""


def _run_picker_cmd(
    runner: Callable[..., subprocess.CompletedProcess[str]], cmd: list[str]
) -> Path | None:
    picked = _run_stdout_cmd(runner, cmd)
    return Path(picked) if picked else None


def _run_menu_cmd(
    runner: Callable[..., subprocess.CompletedProcess[str]],
    prefix: list[str],
    items: list[tuple[str, str]],
) -> str | None:
    flat = [v for pair in items for v in pair]
    return _run_stdout_cmd(runner, [*prefix, *flat]) or None


@dataclass
class KdialogBackend:
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run

    def choose_action(
        self, title: str, status_line: str, items: list[tuple[str, str]]
    ) -> str | None:
        return _run_menu_cmd(self.runner, ["kdialog", "--title", title, "--menu", status_line], items)

    def pick_directory(self, title: str, initial_dir: Path | None) -> Path | None:
        return _run_picker_cmd(
            self.runner,
            ["kdialog", "--title", title, "--getexistingdirectory", str(initial_dir or Path.home())],
        )

    def show_info(self, title: str, message: str) -> None:
        self.runner(["kdialog", "--title", title, "--msgbox", message], check=False)

    def show_error(self, title: str, message: str) -> None:
        self.runner(["kdialog", "--title", title, "--error", message], check=False)

    def show_report(self, title: str, body: str) -> None:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as tf:
            tf.write(body)
            tname = tf.name
        try:
            self.runner(["kdialog", "--title", title, "--textbox", tname, "740", "540"], check=False)
        finally:
            Path(tname).unlink(missing_ok=True)


@dataclass
class ZenityBackend:
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run

    def choose_action(
        self, title: str, status_line: str, items: list[tuple[str, str]]
    ) -> str | None:
        prefix = [
            "zenity",
            "--list",
            f"--title={title}",
            f"--text={status_line}",
            "--width=660",
            "--height=400",
            "--hide-column=1",
            "--column=ID",
            "--column=Действие",
        ]
        return _run_menu_cmd(self.runner, prefix, items)

    def pick_directory(self, title: str, initial_dir: Path | None) -> Path | None:
        return _run_picker_cmd(
            self.runner,
            [
                "zenity",
                "--file-selection",
                "--directory",
                f"--title={title}",
                f"--filename={initial_dir or Path.home()}/",
            ],
        )

    def show_info(self, title: str, message: str) -> None:
        self.runner(["zenity", "--info", f"--title={title}", "--width=520", f"--text={message}"], check=False)

    def show_error(self, title: str, message: str) -> None:
        self.runner(["zenity", "--error", f"--title={title}", "--width=520", f"--text={message}"], check=False)

    def show_report(self, title: str, body: str) -> None:
        self.runner(
            ["zenity", "--text-info", f"--title={title}", "--width=740", "--height=540"],
            input=body,
            text=True,
            check=False,
        )


def detect_gui_backend(
    env: dict[str, str] | None = None,
    which: Callable[[str], str | None] = shutil.which,
) -> GuiBackend | None:
    cur = env if env is not None else dict(os.environ)
    if not (cur.get("DISPLAY") or cur.get("WAYLAND_DISPLAY")):
        return None
    if which("kdialog"):
        return KdialogBackend()
    if which("zenity"):
        return ZenityBackend()
    return None


def _gui_pick_game(cur_game: Path | None, gui: GuiBackend, title: str) -> Path | None:
    picked = gui.pick_directory("Выберите папку We March as One", cur_game)
    norm = normalize_game_dir(picked)
    if is_valid_game_dir(norm):
        return norm
    if picked is not None:
        gui.show_error(title, f"В выбранной папке не найдены файлы We March as One:\n{picked}")
    return None


def _gui_apply_mutation(
    action: str,
    payload: PayloadBundle,
    game_dir: Path,
    steam_dir: Path | None,
    gui: GuiBackend,
    title: str,
) -> None:
    logs: list[str] = []
    try:
        if action in ("install", "install_noach"):
            install_russifier(
                payload,
                game_dir,
                steam_dir,
                logs.append,
                enable_achievements_ui=(action == "install"),
            )
        else:
            uninstall_russifier(game_dir, steam_dir, logs.append)
        gui.show_info(title, "\n".join(logs))
    except Exception as exc:
        gui.show_error(title, str(exc))


def _dispatch_gui_choice(
    choice: str,
    payload: PayloadBundle,
    game_dir: Path | None,
    steam_dir: Path | None,
    gui: GuiBackend,
    title: str,
) -> tuple[Path | None, Path | None]:
    if choice == "browse":
        picked = _gui_pick_game(game_dir, gui, title)
        return (picked, steam_dir or detect_steam_dir(None, picked)) if picked else (game_dir, steam_dir)
    if choice == "achievements":
        gui.show_report(title, format_achievements_report(payload, steam_dir))
        return game_dir, steam_dir
    resolved = game_dir if is_valid_game_dir(game_dir) else _gui_pick_game(game_dir, gui, title)
    if resolved is None:
        return game_dir, steam_dir
    eff_steam = steam_dir or detect_steam_dir(None, resolved)
    _gui_apply_mutation(choice, payload, resolved, eff_steam, gui, title)
    return resolved, eff_steam


def _state_label(game_dir: Path | None, missing_text: str) -> str:
    if not is_valid_game_dir(game_dir):
        return missing_text
    return "Установлен" if is_russifier_installed(game_dir) else "Не установлен"


def run_gui_menu(
    payload: PayloadBundle,
    game_dir: Path | None,
    steam_dir: Path | None,
    gui: GuiBackend,
) -> int:
    title = f"We March as One: Русификатор v{payload.version[:3]}"
    cur_game, cur_steam = game_dir, steam_dir
    while True:
        profiles = load_user_profiles(cur_steam, len(payload.achievements))
        unlocked = profiles[0].unlocked_count if profiles else 0
        state_lbl = _state_label(cur_game, "Папка игры не выбрана")
        status_txt = f"Состояние: {state_lbl}\nПапка игры: {cur_game or 'не найдена'}"
        items = [
            ("install", "Установить русификатор (с кнопкой «Достижения»)"),
            ("install_noach", "Установить без кнопки «Достижения» в меню"),
            ("uninstall", "Вернуть оригинал"),
            ("achievements", f"Достижения ({unlocked} / {len(payload.achievements)})"),
            ("browse", "Выбрать папку с игрой..."),
            ("exit", "Выход"),
        ]
        choice = gui.choose_action(title, status_txt, items)
        if not choice or choice == "exit":
            return 0
        cur_game, cur_steam = _dispatch_gui_choice(choice, payload, cur_game, cur_steam, gui, title)


def _prompt_game_dir_cli(stdin: TextIO, stdout: TextIO) -> Path | None:
    stdout.write("Введите путь к папке We March as One: ")
    stdout.flush()
    norm = normalize_game_dir(stdin.readline())
    if is_valid_game_dir(norm):
        return norm
    stdout.write("ОШИБКА: В указанной папке не найдены файлы We March as One.\n")
    return None


def _run_terminal_action(
    choice: str,
    payload: PayloadBundle,
    game_dir: Path,
    steam_dir: Path | None,
    stdout: TextIO,
) -> None:
    log_fn = lambda msg: stdout.write(msg + "\n")  # noqa: E731
    try:
        if choice in ("1", "2"):
            install_russifier(
                payload, game_dir, steam_dir, log_fn, enable_achievements_ui=(choice == "1")
            )
        else:
            uninstall_russifier(game_dir, steam_dir, log_fn)
    except Exception as exc:
        stdout.write(f"ОШИБКА: {exc}\n")


def _terminal_mutate_selected(
    choice: str,
    payload: PayloadBundle,
    game_dir: Path | None,
    steam_dir: Path | None,
    stdin: TextIO,
    stdout: TextIO,
) -> tuple[Path | None, Path | None]:
    resolved = game_dir if is_valid_game_dir(game_dir) else _prompt_game_dir_cli(stdin, stdout)
    if resolved is None:
        return game_dir, steam_dir
    eff_steam = steam_dir or detect_steam_dir(None, resolved)
    _run_terminal_action(choice, payload, resolved, eff_steam, stdout)
    return resolved, eff_steam


def _dispatch_terminal_choice(
    choice: str,
    payload: PayloadBundle,
    game_dir: Path | None,
    steam_dir: Path | None,
    stdin: TextIO,
    stdout: TextIO,
) -> tuple[Path | None, Path | None, bool]:
    if choice in ("0", "q", "exit"):
        return game_dir, steam_dir, True
    if choice == "4":
        stdout.write("\n" + format_achievements_report(payload, steam_dir) + "\n")
        return game_dir, steam_dir, False
    if choice == "5":
        picked = _prompt_game_dir_cli(stdin, stdout)
        return (picked, steam_dir or detect_steam_dir(None, picked), False) if picked else (game_dir, steam_dir, False)
    if choice in ("1", "2", "3"):
        g_dir, s_dir = _terminal_mutate_selected(
            choice, payload, game_dir, steam_dir, stdin, stdout
        )
        return g_dir, s_dir, False
    return game_dir, steam_dir, False


def run_terminal_menu(
    payload: PayloadBundle,
    game_dir: Path | None,
    steam_dir: Path | None,
    *,
    stdin: TextIO = sys.stdin,
    stdout: TextIO = sys.stdout,
) -> int:
    cur_game, cur_steam = game_dir, steam_dir
    while True:
        profiles = load_user_profiles(cur_steam, len(payload.achievements))
        unlocked = profiles[0].unlocked_count if profiles else 0
        valid = is_valid_game_dir(cur_game)
        state = _state_label(cur_game, "укажите папку игры")
        stdout.write(
            f"\n=== We March as One: Русификатор v{payload.version[:3]} (Steam Deck / Linux) ===\n"
            f"Папка игры: {cur_game if valid else 'не найдена'}\n"
            f"Состояние:  {state}\n"
            f"1) Установить русификатор (с кнопкой «Достижения»)\n"
            f"2) Установить без кнопки «Достижения» в меню\n"
            f"3) Вернуть оригинал\n"
            f"4) Достижения ({unlocked} / {len(payload.achievements)})\n"
            f"5) Указать папку с игрой\n"
            f"0) Выход\n"
            f"Выберите пункт [0-5]: "
        )
        stdout.flush()
        raw = stdin.readline()
        if not raw:
            return 0
        cur_game, cur_steam, done = _dispatch_terminal_choice(
            raw.strip(), payload, cur_game, cur_steam, stdin, stdout
        )
        if done:
            return 0


def _build_cli_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="install-steamdeck.sh",
        description="Русификатор текста, шрифтов и достижений для We March as One (Steam Deck / Linux)",
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--install", action="store_true", help="Установить русификатор")
    group.add_argument(
        "--uninstall", "--restore", dest="uninstall", action="store_true", help="Вернуть оригинал"
    )
    group.add_argument("--status", action="store_true", help="Показать состояние установки и пути")
    group.add_argument("--achievements", action="store_true", help="Показать список достижений Steam")
    parser.add_argument("--no-achievements", action="store_true", help="Без кнопки «Достижения» в меню")
    parser.add_argument("--skip-assembly", action="store_true", help="Не менять Assembly-CSharp.dll")
    parser.add_argument("--game-dir", type=Path, default=None, help="Путь к папке We March as One")
    parser.add_argument("--steam-dir", type=Path, default=None, help="Путь к папке клиента Steam")
    parser.add_argument("--payload", type=Path, default=None, help="Путь к Setup.exe или payload.gz")
    return parser


def _require_cli_game_dir(
    explicit_game: Path | None, steam_dir: Path | None, script_dir: Path
) -> Path:
    if explicit_game is not None:
        norm = normalize_game_dir(explicit_game)
        if not is_valid_game_dir(norm) or norm is None:
            raise FileNotFoundError(
                f"В указанной папке не найдены файлы We March as One: {explicit_game}"
            )
        return norm
    detected = detect_game_dir(None, steam_dir, script_dir)
    if detected is None:
        raise FileNotFoundError(
            "Не удалось автоматически найти папку We March as One. Укажите --game-dir."
        )
    return detected


def _run_cli_mutation(
    args: argparse.Namespace,
    payload: PayloadBundle,
    steam_dir: Path | None,
    script_dir: Path,
    stdout: TextIO,
) -> int:
    game_dir = _require_cli_game_dir(args.game_dir, steam_dir, script_dir)
    eff_steam = steam_dir or detect_steam_dir(None, game_dir)
    writer = lambda s: stdout.write(s + "\n")  # noqa: E731
    if args.install:
        install_russifier(
            payload,
            game_dir,
            eff_steam,
            writer,
            enable_achievements_ui=not args.no_achievements,
            skip_assembly=bool(args.skip_assembly),
        )
    else:
        uninstall_russifier(game_dir, eff_steam, writer)
    return 0


def _run_interactive_cli(
    explicit_game: Path | None,
    payload: PayloadBundle,
    steam_dir: Path | None,
    script_dir: Path,
    stdin: TextIO,
    stdout: TextIO,
    gui_override: GuiBackend | None = None,
) -> int:
    game_dir = detect_game_dir(explicit_game, steam_dir, script_dir)
    eff_steam = steam_dir or detect_steam_dir(None, game_dir)
    gui = gui_override if gui_override is not None else detect_gui_backend()
    if gui is not None:
        return run_gui_menu(payload, game_dir, eff_steam, gui)
    return run_terminal_menu(payload, game_dir, eff_steam, stdin=stdin, stdout=stdout)


def _dispatch_cli(
    args: argparse.Namespace,
    payload: PayloadBundle,
    steam_dir: Path | None,
    script_dir: Path,
    stdin: TextIO,
    stdout: TextIO,
) -> int:
    if args.status:
        game_dir = detect_game_dir(args.game_dir, steam_dir, script_dir)
        report, code = format_status_report(
            payload, game_dir, steam_dir or detect_steam_dir(None, game_dir)
        )
        stdout.write(report)
        return code
    if args.achievements:
        stdout.write(format_achievements_report(payload, steam_dir))
        return 0
    if args.install or args.uninstall:
        return _run_cli_mutation(args, payload, steam_dir, script_dir, stdout)
    return _run_interactive_cli(args.game_dir, payload, steam_dir, script_dir, stdin, stdout)


def run_cli(
    argv: Sequence[str] | None = None,
    *,
    script_path: Path | None = None,
    stdin: TextIO = sys.stdin,
    stdout: TextIO = sys.stdout,
    stderr: TextIO = sys.stderr,
) -> int:
    args = _build_cli_parser().parse_args(argv)
    eff_script = (script_path or Path(__file__)).resolve()
    script_dir = eff_script if eff_script.is_dir() else eff_script.parent
    try:
        payload = load_payload(args.payload, eff_script)
        steam_dir = detect_steam_dir(args.steam_dir, args.game_dir)
        return _dispatch_cli(args, payload, steam_dir, script_dir, stdin, stdout)
    except Exception as exc:
        stderr.write(f"ОШИБКА: {exc}\n")
        return 1


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(run_cli())
