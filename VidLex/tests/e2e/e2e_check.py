#!/usr/bin/env python3
"""
Сквозные тесты VidLex: настоящий FFmpeg + whisper.cpp + конвейер VidLex (через VidLex.Cli).

Пример (Linux):
  python3 e2e_check.py --cli "dotnet ../../tools/VidLex.Cli/bin/Release/net8.0/VidLex.Cli.dll" \
      --media media --work /tmp/vidlex-e2e --model ggml-small.bin --whisper whisper-cli
Пример (Windows):
  python e2e_check.py --cli "..\\..\\tools\\VidLex.Cli\\bin\\Release\\net48\\VidLex.Cli.exe" --media media --work %TEMP%\\e2e ...

Код возврата 0 — все проверки пройдены.
"""
import argparse, os, re, shlex, shutil, subprocess, sys, time

S = ["Сегодня мы рассмотрим основные принципы работы программы.", "Затем перейдём к практическим примерам.",
     "Программа находит все видеофайлы в выбранной папке и во всех вложенных каталогах.",
     "Для каждого видео создаётся текстовый файл с расшифровкой.",
     "Если включены временные метки, перед каждой фразой указывается время.",
     "Обработка выполняется на вашем компьютере без отправки данных в интернет.",
     "Сначала программа извлекает звуковую дорожку из видео.", "После этого запускается распознавание русской речи.",
     "Готовый текст проходит проверку пунктуации и орфографии.", "В конце результат сохраняется в файл в кодировке ю тэ эф восемь."]
SHORT = " ".join(S[0:3]); LESSON = " ".join(S[5:8]); LONG = " ".join(S * 3)

# Относительный путь TXT (без расширения) → (эталон, допустимый WER).
# Речь синтезирована espeak-ng, поэтому пороги проверяют, что распознано содержание (а не мусор или пустота),
# а не точность модели. На роликах с фразами 4–5 модель small ошибается почти во всех словах (это видно и при
# прямом запуске whisper-cli), поэтому для них WER не проверяется — только формат файла.
# «Урок 01.mp4» и «Урок 01.mkv» лежат в одной папке: по правилу коллизий mkv получает «Урок 01.txt», mp4 — «Урок 01 [mp4].txt».
SPEECH = {
    "Урок 01 [mp4]": (SHORT, 0.6),
    "Курс/Модуль 1/Урок 01": (LESSON, 0.6),
    "Разное/старое": (SHORT, 0.6),
    "Разное/clip": (LESSON, 0.6),
    "Длинная лекция": (LONG, 0.55),
    "Урок 01": (None, None),
    "Разное/Видео (часть 1) — №1 & 'тест' [v2]": (None, None),
    "Курс/Модуль 2/Урок 01": (None, None),
}

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("  [OK]   " if ok else "  [FAIL] ") + name + (f" — {detail}" if detail else ""), flush=True)


def norm(t):
    t = t.lower().replace("ё", "е")
    t = re.sub(r"\[[^\]]*\]", " ", t)
    return re.findall(r"[а-яa-z0-9]+", t)


def wer(ref, hyp):
    r, h = norm(ref), norm(hyp)
    d = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(h) + 1):
            cur = d[j]
            d[j] = min(d[j] + 1, d[j - 1] + 1, prev + (r[i - 1] != h[j - 1]))
            prev = cur
    return d[len(h)] / max(1, len(r))


def run(args, cli, common, extra, expect_code=None, timeout=3600):
    cmd = shlex.split(cli, posix=(os.name != "nt")) + common + extra
    t0 = time.time()
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    out = p.stdout + p.stderr
    print(f"    $ VidLex.Cli {' '.join(extra)}  → код {p.returncode} за {time.time() - t0:.0f} с", flush=True)
    if expect_code is not None and p.returncode != expect_code:
        print(out[-4000:])
    return p.returncode, out


def read_txt(path):
    raw = open(path, "rb").read()
    return raw, raw.decode("utf-8-sig")


def status_lines(out):
    """Разбор блока «Итог:» из вывода VidLex.Cli: {относительный путь: статус}."""
    res = {}
    if "Итог:" not in out:
        return res
    for line in out.split("Итог:", 1)[1].splitlines():
        m = re.match(r"\s+(\S+)\s+(.+?\.\w+)(?:\s+→|\s+\[|\s+\(|$)", line)
        if m:
            res[m.group(2).replace("\\", "/")] = m.group(1)
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cli", required=True)
    ap.add_argument("--media", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--whisper", default="")
    ap.add_argument("--ffmpeg", default="")
    ap.add_argument("--ffprobe", default="")
    a = ap.parse_args()

    work = os.path.abspath(a.work)
    shutil.rmtree(work, ignore_errors=True)
    src = os.path.join(work, "src")
    shutil.copytree(a.media, src)
    data = os.path.join(work, "data")
    common = ["--model", os.path.abspath(a.model), "--data-dir", data, "--chunk", "60"]
    if a.whisper: common += ["--whisper", a.whisper]
    if a.ffmpeg: common += ["--ffmpeg", a.ffmpeg]
    if a.ffprobe: common += ["--ffprobe", a.ffprobe]

    # ---------- 1. Основной прогон: рядом с видео, без меток ----------
    print("\n== Сценарий 1: обработка папки с вложенными каталогами (результаты рядом с видео)")
    code, out = run(a, a.cli, common, ["--source", src])
    st = status_lines(out)
    check("код возврата 1 (есть повреждённые файлы, остальные обработаны)", code == 1, f"код {code}")
    for rel, (ref, limit) in SPEECH.items():
        txt = os.path.join(src, rel + ".txt")
        if not os.path.exists(txt):
            check(f"TXT создан: {rel}.txt", False)
            continue
        raw, text = read_txt(txt)
        check(f"TXT создан рядом с видео: {rel}.txt", True)
        if ref is not None:
            w = wer(ref, text)
            check(f"WER ≤ {limit}: {rel}", w <= limit, f"WER={w:.2f}")
        check(f"UTF-8 с BOM и CRLF: {rel}", raw.startswith(b"\xef\xbb\xbf") and b"\r\n" in raw)
        check(f"кириллица читается: {rel}", re.search(r"[а-яА-ЯёЁ]{3,}", text) is not None)
        glued = re.findall(r"[а-яё][.!?…][А-ЯЁа-яё]", text)
        check(f"нет склеенных фраз (точка без пробела): {rel}", not glued, str(glued[:3]))
        longw = [x for x in re.findall(r"\S+", text) if len(x) > 30]
        check(f"нет слипшихся слов: {rel}", not longw, str(longw[:3]))

    sil = os.path.join(src, "Разное", "тишина.txt")
    check("видео без речи → «[Речь не обнаружена]»", os.path.exists(sil) and read_txt(sil)[1].strip() == "[Речь не обнаружена]")
    edges = os.path.join(src, "Разное", "тишина по краям.txt")
    if os.path.exists(edges):
        w = wer(" ".join(S[0:2]), read_txt(edges)[1])
        check("тишина в начале и в конце не мешает распознаванию", w <= 0.45, f"WER={w:.2f}")
    check("повреждённое видео → ошибка, TXT не создан", not os.path.exists(os.path.join(src, "Разное", "битое.txt"))
          and st.get("Разное/битое.mp4") == "Ошибка", st.get("Разное/битое.mp4", "нет статуса"))
    check("видео без звука → пропущено", st.get("Разное/без звука.mp4") == "Пропущено", st.get("Разное/без звука.mp4", "нет статуса"))
    check("неподдерживаемые форматы не попали в очередь", "заметки.txt" not in out and "запись.xyz" not in out)
    check("ошибка одного файла не остановила очередь", sum(1 for s in st.values() if s == "Завершено") >= 4,
          f"завершено {sum(1 for s in st.values() if s == 'Завершено')}")
    temp = os.path.join(data, "Temp")
    check("временные файлы удалены", not os.path.isdir(temp) or not os.listdir(temp), str(os.listdir(temp) if os.path.isdir(temp) else ""))
    logs = os.path.join(data, "Logs")
    log_text = "".join(open(os.path.join(logs, f), encoding="utf-8").read() for f in os.listdir(logs))
    check("журнал: уровни INFO и ERROR, этапы и имена файлов", "| INFO    |" in log_text and "| ERROR   |" in log_text and "битое.mp4" in log_text)
    check("журнал не содержит текст расшифровок", "основные принципы работы программы" not in log_text.lower())

    # ---------- 2. Повторный запуск ----------
    print("\n== Сценарий 2: повторный запуск (результаты уже есть, политика «Пропускать»)")
    mtimes = {rel: os.path.getmtime(os.path.join(src, rel + ".txt")) for rel in SPEECH if os.path.exists(os.path.join(src, rel + ".txt"))}
    code, out = run(a, a.cli, common, ["--source", src, "--existing", "Skip"])
    st2 = status_lines(out)
    check("уже обработанные видео пропущены, а не ошибочны", all(st2.get(k) == "Пропущено" for k in st2 if k.startswith("Урок 01") or k.startswith("Курс/Модуль 1")),
          str({k: v for k, v in st2.items() if not k.startswith("Разное")}))
    check("существующие TXT не перезаписаны", all(os.path.getmtime(os.path.join(src, r + ".txt")) == m for r, m in mtimes.items()))

    # ---------- 3. Отдельная папка + временные метки ----------
    print("\n== Сценарий 3: все результаты в одну папку, временные метки включены")
    out_dir = os.path.join(work, "Результаты")
    before = set(os.path.relpath(os.path.join(dp, f), src) for dp, _, fs in os.walk(src) for f in fs)
    code, out = run(a, a.cli, common, ["--source", src, "--output-folder", out_dir, "--timestamps", "--existing", "Overwrite"])
    files = sorted(f for f in os.listdir(out_dir) if f.endswith(".txt")) if os.path.isdir(out_dir) else []
    print("    файлы в папке результатов:", files)
    check("подпапки не воспроизводятся", os.path.isdir(out_dir) and all(not os.path.isdir(os.path.join(out_dir, f)) for f in os.listdir(out_dir)))
    expected = {"Урок 01 (Курс - Модуль 1).txt", "Урок 01 (Курс - Модуль 2).txt", "Урок 01 (mkv).txt", "Урок 01 (mp4).txt"}
    check("одинаковые имена из разных подпапок не конфликтуют", expected.issubset(files), str(sorted(expected - set(files))))
    after = set(os.path.relpath(os.path.join(dp, f), src) for dp, _, fs in os.walk(src) for f in fs)
    check("исходные папки не изменены", before == after, str(after - before))
    lt = os.path.join(out_dir, "Длинная лекция.txt")
    if os.path.exists(lt):
        lines = [l for l in read_txt(lt)[1].splitlines() if l.strip()]
        check("каждая строка начинается с метки [ЧЧ:ММ:СС]", lines and all(re.match(r"^\[\d\d:\d\d:\d\d\] \S", l) for l in lines), lines[0] if lines else "")
        stamps = [int(l[1:3]) * 3600 + int(l[4:6]) * 60 + int(l[7:9]) for l in lines]
        check("метки возрастают и не выходят за длительность видео (2:19)", stamps == sorted(stamps) and stamps[-1] <= 140, f"{stamps[:3]}…{stamps[-1]}")
    else:
        check("TXT с метками создан", False)

    # ---------- 4. Отсутствующая модель ----------
    print("\n== Сценарий 4: недоступная модель")
    bad = [x for x in common]
    bad[bad.index("--model") + 1] = os.path.join(work, "нет-такой-модели.bin")
    code, out = run(a, a.cli, bad, ["--source", src], expect_code=2)
    check("понятная ошибка и код 2 при отсутствии модели", code == 2 and "модел" in out.lower(), out.strip().splitlines()[-1] if out.strip() else "")

    # ---------- 5. Недоступная папка результатов ----------
    print("\n== Сценарий 5: недоступная целевая папка")
    blocker = os.path.join(work, "файл-вместо-папки")
    open(blocker, "w").close()
    code, out = run(a, a.cli, common, ["--source", src, "--output-folder", os.path.join(blocker, "out")], expect_code=2)
    check("обработка не начинается, если папка результатов недоступна", code == 2 and "Нет доступа" in out)

    # ---------- 6. Остановка и продолжение по контрольной точке ----------
    print("\n== Сценарий 6: остановка во время обработки и продолжение")
    lec = os.path.join(work, "lecture")
    os.makedirs(lec)
    shutil.copy(os.path.join(src, "Длинная лекция.mp4"), lec)
    code, out = run(a, a.cli, common, ["--source", lec, "--stop-after-seconds", "14", "--keep-checkpoints", "--verbose"])
    check("остановка: задание помечено «Остановлено», TXT не создан",
          "Остановлено" in out and not os.path.exists(os.path.join(lec, "Длинная лекция.txt")))
    code, out = run(a, a.cli, common, ["--source", lec, "--verbose"])
    check("повторный запуск продолжил с контрольной точки", "Восстановление после сбоя" in out, "")
    t = os.path.join(lec, "Длинная лекция.txt")
    w = wer(LONG, read_txt(t)[1]) if os.path.exists(t) else 1
    check("после продолжения расшифровка полная", os.path.exists(t) and w <= 0.55, f"WER={w:.2f}")

    # ---------- 7. Пауза ----------
    print("\n== Сценарий 7: пауза и продолжение")
    pz = os.path.join(work, "pause")
    os.makedirs(pz)
    shutil.copy(os.path.join(src, "Длинная лекция.mp4"), pz)
    code, out = run(a, a.cli, common, ["--source", pz, "--pause-test", "8"])
    m = re.findall(r"очередь ([0-9.,]+)%.*?состояние: (\w+)", out)
    paused = [float(p.replace(",", ".")) for p, s in m if s == "Paused"]
    check("пауза: состояние «Приостановлено», прогресс не растёт", len(paused) >= 2 and max(paused) - min(paused) < 0.5, str(paused))
    check("после паузы обработка завершена", "Завершено" in out and os.path.exists(os.path.join(pz, "Длинная лекция.txt")))

    failed = [r for r in results if not r[1]]
    print(f"\nИтого проверок: {len(results)}, успешно: {len(results) - len(failed)}, неуспешно: {len(failed)}")
    for n, _, d in failed:
        print(f"  НЕ ПРОЙДЕНО: {n} {d}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
