def format_elapsed(seconds: float) -> str:
    m = int(seconds // 60)
    s = int(seconds % 60)
    ms = int((seconds * 1000) % 1000)
    return f"{m:02d}:{s:02d}.{ms:03d}"