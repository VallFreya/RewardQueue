# Разработка

## Требования

- .NET SDK 10.
- Visual Studio с компонентом «Desktop development with C++» (нужен для линковки NativeAOT).
- OBS Studio 32 (Windows).

## Тесты

Тесты не требуют OBS: исходники плагина подключаются в тестовый проект как есть.

```powershell
cd tests\RewardQueue.Tests
dotnet test
```

## Сборка

```powershell
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
dotnet publish -c Release -r win-x64
```

Результат: `bin\Release\net10.0-windows\win-x64\publish\RewardQueue.dll`. Если линковка падает из-за `vswhere.exe`, добавьте папку установщика Visual Studio в PATH, как показано выше.

## Установка в OBS

Скопируйте `RewardQueue.dll` в папку `obs-plugins\64bit` установленной OBS (OBS при этом должен быть закрыт) и запустите OBS. Окно настроек откроется из меню Tools.

## Журнал

`%LOCALAPPDATA%\Temp\RewardQueue_debug.log`. В журнал пишутся только события; ников и сообщений зрителей, токенов и ключа доступа там нет.

## Правила кода

- Плагин собирается NativeAOT: никакой рефлексии, JSON только через source generator, делегаты, переданные в нативный код, лежат в `static readonly` полях.
- Исключения не выходят из экспортов модуля и из колбэков, вызываемых OBS; таймеры и сетевые обработчики обёрнуты в try/catch.
- Логика очереди, разбор сообщений Twitch и автоматы подключения держатся отдельно от OBS и покрыты тестами: позже плагин планируется перенести на C++.
