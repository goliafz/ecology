#!/bin/bash
# Компактный вариант (160×90, 2 кадра/с) — файлы небольшие и хранятся в репозитории для CI на Windows.
# Требуется: espeak-ng, ffmpeg. Запуск: ./make_media.sh media
# Генерация тестовых видео с русской речью (espeak-ng) для сквозных тестов VidLex.
set -e
OUT="$1"; rm -rf "$OUT"; mkdir -p "$OUT"
T=$(mktemp -d)
say() { espeak-ng -v ru -s 150 -w "$T/$1.raw.wav" "$2"; ffmpeg -loglevel error -y -i "$T/$1.raw.wav" -ar 16000 -ac 1 "$T/$1.wav"; }
sil() { ffmpeg -loglevel error -y -f lavfi -i anullsrc=r=16000:cl=mono -t "$2" "$T/$1.wav"; }
cat_wav() { local out=$1; shift; : > "$T/list.txt"; for f in "$@"; do echo "file '$T/$f.wav'" >> "$T/list.txt"; done; ffmpeg -loglevel error -y -f concat -safe 0 -i "$T/list.txt" -c pcm_s16le "$T/$out.wav"; }
video() { # $1 audio wav name, $2 output path, extra args
  local a="$T/$1.wav"; local o="$2"; shift 2
  local d=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$a")
  ffmpeg -loglevel error -y -f lavfi -i "color=c=0x101214:s=160x90:r=2:d=$d" -i "$a" -shortest "$@" -b:a 48k "$o"
}
say s1 "Сегодня мы рассмотрим основные принципы работы программы."
say s2 "Затем перейдём к практическим примерам."
say s3 "Программа находит все видеофайлы в выбранной папке и во всех вложенных каталогах."
say s4 "Для каждого видео создаётся текстовый файл с расшифровкой."
say s5 "Если включены временные метки, перед каждой фразой указывается время."
say s6 "Обработка выполняется на вашем компьютере без отправки данных в интернет."
say s7 "Сначала программа извлекает звуковую дорожку из видео."
say s8 "После этого запускается распознавание русской речи."
say s9 "Готовый текст проходит проверку пунктуации и орфографии."
say s10 "В конце результат сохраняется в файл в кодировке ю тэ эф восемь."
say runon "Сегодня мы изучим программу далее рассмотрим настройки после этого начнём работу"
sil p05 0.5; sil p1 1.2; sil p10 10
cat_wav short s1 p1 s2 p1 s3
cat_wav short2 s4 p1 s5
cat_wav lesson s6 p1 s7 p1 s8
# Длинная речь (~3 мин): все фразы несколько раз с паузами.
LONG=(); for r in 1 2 3; do for k in 1 2 3 4 5 6 7 8 9 10; do LONG+=(s$k p05); done; done
cat_wav long "${LONG[@]}"
cat_wav quiet_edges p10 s1 p1 s2 p10
cat_wav runon_a p05 runon p05
# Речь на фоне шума без пауз — для проверки жёсткой резки с перекрытием.
ffmpeg -loglevel error -y -i "$T/long.wav" -f lavfi -i "anoisesrc=color=pink:amplitude=0.05:r=16000" -filter_complex "[0][1]amix=inputs=2:duration=first:normalize=0" -ar 16000 -ac 1 "$T/noisy.wav"
sil silence 20

mkdir -p "$OUT/Курс/Модуль 1" "$OUT/Курс/Модуль 2" "$OUT/Глубоко/a/b/c/d" "$OUT/Разное"
video short "$OUT/Урок 01.mp4" -c:v libx264 -preset ultrafast -c:a aac
video short2 "$OUT/Урок 01.mkv" -c:v libx264 -preset ultrafast -c:a libopus
video lesson "$OUT/Курс/Модуль 1/Урок 01.mp4" -c:v libx264 -preset ultrafast -c:a aac
video short2 "$OUT/Курс/Модуль 2/Урок 01.mp4" -c:v libx264 -preset ultrafast -c:a aac
video short "$OUT/Глубоко/a/b/c/d/глубокое видео.mov" -c:v libx264 -preset ultrafast -c:a aac
video short2 "$OUT/Разное/Видео (часть 1) — №1 & 'тест' [v2].webm" -c:v libvpx -deadline realtime -c:a libopus
video lesson "$OUT/Разное/clip.avi" -c:v mpeg4 -c:a mp3
video short "$OUT/Разное/старое.wmv" -c:v wmv2 -c:a wmav2
video runon_a "$OUT/Разное/слитно.flv" -c:v flv -c:a mp3 -ar 22050
video silence "$OUT/Разное/тишина.mpg" -c:v mpeg2video -c:a mp2
video quiet_edges "$OUT/Разное/тишина по краям.m4v" -c:v libx264 -preset ultrafast -c:a aac
video short "$OUT/Разное/телефон.3gp" -c:v libx264 -preset ultrafast -c:a aac -ar 16000
video short2 "$OUT/Разное/эфир.ts" -c:v mpeg2video -c:a mp2
video long "$OUT/Длинная лекция.mp4" -c:v libx264 -preset ultrafast -c:a aac
video noisy "$OUT/Лекция с шумом.mpeg" -c:v mpeg2video -c:a mp2 -f mpeg
# Видео без звука.
ffmpeg -loglevel error -y -f lavfi -i "color=c=black:s=160x90:r=2:d=5" -c:v libx264 -preset ultrafast "$OUT/Разное/без звука.mp4"
# Две аудиодорожки: первая — английская тишина, вторая — русская речь.
ffmpeg -loglevel error -y -f lavfi -i "color=c=black:s=160x90:r=2:d=8" -f lavfi -i "anullsrc=r=16000:cl=mono" -i "$T/short2.wav" -map 0:v -map 1:a -map 2:a -t 8 -c:v libx264 -preset ultrafast -c:a aac -metadata:s:a:0 language=eng -metadata:s:a:1 language=rus "$OUT/Разное/две дорожки.mkv"
# Повреждённое видео: обрезанный mp4 и случайные байты.
head -c 3000 "$OUT/Урок 01.mp4" > "$OUT/Разное/битое.mp4"
head -c 50000 /dev/urandom > "$OUT/Разное/мусор.mov"
# Неподдерживаемые форматы — не должны попасть в очередь.
echo "заметки" > "$OUT/Разное/заметки.txt"; head -c 1000 /dev/urandom > "$OUT/Разное/запись.xyz"
rm -rf "$T"
find "$OUT" -type f | sort
