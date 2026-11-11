# LiveRaid 1.2.4 - install / установка

## English

**One archive, two equal folders:** `LiveRaid-1.2.4.zip` holds `Single` (single player, SPT without Fika) and `Online` (**LiveRaid Online**, Project Fika co-op, [docs/FIKA.md](docs/FIKA.md), `FIKA.md` in the archive). **Unpack the contents of ONE of the two folders into the game's root** (not the folder itself and not both). Online needs Project Fika installed; if Fika is installed, the single-player edition switches itself off and says in the log that Online is needed. To change over later, unpack the other folder the same way, or start `LiveRaid Launcher.exe` (it is in both folders): it switches between "Single player" and "Online" (puts the right client DLL in place and moves Fika's files aside or back).

**Requirements:** SPT **4.1.6** (EFT 0.16.9) with these mods installed first: QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0 (LiveRaid Online also needs Project Fika 2.4.x with QuestingBots Fika sync). Remove **ORBIT** - it conflicts. **Strongly recommended: Smoothie 1.1.0** (by morphox, https://sp-mod.com/mod/3132/smoothie) - it renders only what you actually see; a big performance gain with no downside for LiveRaid (with 20+ bots alive it helps most). Many thanks to its author.

1. **Close the game and stop the SPT server** (a running server holds its DLLs).
2. Unpack the contents of the chosen folder (`Single` or `Online`) **into the game's root folder** (where `EscapeFromTarkov.exe` and `BepInEx` are), agreeing to replace files. It holds:
   - `BepInEx/plugins/LiveRaid/LiveRaid.Client.dll` - the client plugin;
   - `SPT_Runtime/user/mods/LiveRaid/` - the server mod (`LiveRaid.Server.dll`, `data/*.json`);
   - `LiveRaid Launcher.exe` and `LiveRaid Launcher/editions` - the mode switch (both editions are kept there, one works in the game).

   > If your server folder is `SPT` instead of `SPT_Runtime` (the older 4.x layout `SPT/user/mods`), move the contents of `SPT_Runtime/user/mods/LiveRaid` to `SPT/user/mods/LiveRaid`.
3. Start the server and the game as usual.

Nothing needs deleting: map caches, recorded routes, learned places and your F12 settings are kept. New settings appear with their default values.

**Check:** `BepInEx/LogOutput.log` must contain `LiveRaid 1.2.4 loaded` (`LiveRaid Online 1.2.4 loaded` for Online) without a `disabled: …` tail. Open **F12** (the Configuration Manager) → LiveRaid: the texts follow the game's language; pick a **preset** at the top of the menu or leave *Recommended*. The language can be forced in the same menu (*Language*).

**Uninstall:** delete `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` and `LiveRaid Launcher`.

## Русский

**Один архив, две равноправные папки:** в `LiveRaid-1.2.4.zip` лежат `Single` (одиночная игра, SPT без Fika) и `Online` (**LiveRaid Online**, кооператив через Project Fika, [docs/FIKA.md](docs/FIKA.md)). **В корень папки игры распакуйте СОДЕРЖИМОЕ одной из двух папок** (не саму папку и не обе). Для Online должен стоять Project Fika; если Fika стоит, одиночная редакция сама выключается и пишет в лог, что нужна Online. Чтобы перейти на другую позже, распакуйте вторую папку так же или запустите `LiveRaid Launcher.exe` (он есть в обеих): он переключает «Одиночный» и «Online» (кладёт нужную DLL и убирает/возвращает файлы Fika).

**Нужно:** SPT **4.1.6** (EFT 0.16.9) и заранее установленные QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0 (для Online ещё Project Fika 2.4.x и QuestingBots Fika sync). **ORBIT** удалите - конфликтует. **Очень рекомендуется: Smoothie 1.1.0** (автор morphox, https://sp-mod.com/mod/3132/smoothie) - рисует только то, что вы реально видите; заметный прирост производительности без ущерба для LiveRaid (с 20+ живыми ботами выигрыш больше всего). Отдельное спасибо автору.

1. **Закройте игру и остановите сервер SPT** (иначе DLL сервера занята).
2. Распакуйте содержимое выбранной папки (`Single` или `Online`) **в корень папки игры** (где `EscapeFromTarkov.exe` и `BepInEx`), согласившись на замену файлов: клиентский плагин `BepInEx/plugins/LiveRaid`, серверный мод `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` и папка `LiveRaid Launcher/editions` (переключатель режимов).
   > Если сервер лежит в `SPT`, а не в `SPT_Runtime`, скопируйте `SPT_Runtime/user/mods/LiveRaid` в `SPT/user/mods/LiveRaid`.
3. Запустите сервер и игру.

Ничего удалять не нужно: кэш карт, маршруты, выученные места и настройки F12 сохраняются. **Проверка:** в `BepInEx/LogOutput.log` есть `LiveRaid 1.2.4 loaded` без хвоста `disabled: …`. В **F12** → LiveRaid тексты на языке игры; вверху меню выберите **пресет** (или оставьте «Рекомендуемый»); язык можно задать там же («Язык / Language»). **Удаление:** удалите `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` и `LiveRaid Launcher`.
