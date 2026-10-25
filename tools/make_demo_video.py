"""Assembles the PlannerDNA demo video from frames written by the Unity DemoVideoRecorder.

    .media-venv\\Scripts\\python.exe tools\\make_demo_video.py

Input:  TestResults/video_frames/{frame_*.jpg, concat.txt}
Output: deliverables/video/PlannerDNA_demo.mp4 (1920x1080, 30 fps, H.264) and a poster image.
"""
from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
FRAMES = ROOT / "TestResults" / "video_frames"
OUT = ROOT / "deliverables" / "video"
FONTS = ROOT / "PlannerDNA_Unity" / "PlannerDNA_Unity" / "Assets" / "PlannerDNA" / "Fonts"
W, H = 1920, 1080
BG, INK, MUTED, ACCENT = (15, 23, 42), (248, 250, 252), (148, 163, 184), (16, 185, 129)


def font(weight: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / f"PublicSans-{weight}.ttf"), size)


def centred(draw: ImageDraw.ImageDraw, y: int, text: str, f: ImageFont.FreeTypeFont, fill) -> int:
    box = draw.textbbox((0, 0), text, font=f)
    draw.text(((W - (box[2] - box[0])) // 2, y), text, font=f, fill=fill)
    return y + (box[3] - box[1])


def card(path: Path, title: str, lines: list[str], footer: str) -> None:
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.ellipse((W // 2 - 34, 250, W // 2 + 34, 318), outline=ACCENT, width=10)
    y = centred(d, 370, title, font("Bold", 96), INK) + 60
    for line in lines:
        y = centred(d, y, line, font("SemiBold", 40), INK) + 26
    centred(d, H - 150, footer, font("Regular", 28), MUTED)
    d.rectangle((W // 2 - 90, H - 190, W // 2 + 90, H - 186), fill=ACCENT)
    img.save(path, quality=95)


def main() -> int:
    concat = FRAMES / "concat.txt"
    if not concat.exists():
        raise SystemExit(f"No recording found at {concat}. Run the Unity DemoVideoRecorder first.")
    OUT.mkdir(parents=True, exist_ok=True)

    card(FRAMES / "intro.jpg", "PlannerDNA",
         ["A local 3D sandbox for urban-planning students",
          "Design two plans, compare them, choose, and test what a small model learned"],
         "Demo recording: the choices shown in this video are scripted")
    card(FRAMES / "outro.jpg", "Runs entirely on one computer",
         ["Unity 6 sandbox  |  FastAPI + SQLite  |  scikit-learn pairwise logistic model",
          "Blinded, pre-registered evaluation with sealed predictions",
          "Figures and reports generated from the exported data"],
         "Synthetic city, synthetic metrics: a learning tool, not planning advice")

    body = concat.read_text(encoding="utf-8")
    full = FRAMES / "concat_full.txt"
    full.write_text("file 'intro.jpg'\nduration 4.5\n" + body.rsplit("file ", 1)[0] +
                    "file 'outro.jpg'\nduration 5.5\nfile 'outro.jpg'\n", encoding="utf-8")

    video = OUT / "PlannerDNA_demo.mp4"
    cmd = [imageio_ffmpeg.get_ffmpeg_exe(), "-y", "-f", "concat", "-safe", "0", "-i", str(full),
           "-vf", f"fps=30,scale={W}:{H}:flags=lanczos:out_range=tv,format=yuv420p",
           "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
           "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-profile:v", "high", "-movflags", "+faststart", str(video)]
    subprocess.run(cmd, check=True, cwd=FRAMES, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    shutil.copyfile(FRAMES / "intro.jpg", OUT / "PlannerDNA_demo_poster.jpg")
    print(f"Wrote {video} ({video.stat().st_size / 1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
