# RecompVault

Launcher para Windows (WPF, .NET 10) de recomps, decomps y ports. Se integra con
[Quiver Launcher](https://github.com/tgeorgiadis/quiver-launcher) para sacar de ahí los
nombres, los iconos, los repositorios y las actualizaciones.

## Estructura esperada

```
Raíz\
  N64\Zelda64Recompiled-v1.2.0-Windows\Zelda64Recompiled.exe
  PSX\Alundra_Recompiled\...
  QuiverLauncher-win-Portable\apps.json      ← se detecta solo
```

- Cada subcarpeta de un sistema es un juego. El ejecutable se busca hasta 3 niveles de profundidad.
- También se muestran las apps instaladas por Quiver fuera de la raíz (sistema "Quiver" o según sus etiquetas).
- Quiver se busca dentro y al lado de la raíz, y también en `%LOCALAPPDATA%\QuiverLauncher`. Si no lo encuentra, el botón **Q Quiver** te deja elegir la carpeta.

## Cómo decide cada cosa

| Qué | Orden |
|---|---|
| Nombre | El que pongas tú → app de Quiver (coincidencia exacta) → `gametitles.json` → nombre de carpeta limpio |
| Ejecutable | El que elijas tú (⚙) → `selected_executable.txt` de Quiver → heurística (descarta instaladores, crash handlers, herramientas de consola, scripts de build…) |
| Versión instalada | **Solo** el `version.txt` de la carpeta del juego, el mismo que escribe Quiver. Si no existe, se muestra como "desconocida" y no se inventa ninguna actualización |
| Última versión | GitHub/GitLab Releases (incluye prereleases, configurable), con la caché de Quiver como respaldo sin conexión |
| Paquete a descargar | `releaseAssetFilter` de Quiver (repos con varios juegos) → mismo patrón que el paquete instalado (`installed-build.txt`) → el mejor paquete de Windows x64 |

`gametitles.json` va incrustado en el exe. Si pones un `gametitles.json` junto al exe o en
`%APPDATA%\RecompVault`, amplía o sobrescribe sus entradas sin necesidad de recompilar.

## Actualizaciones

- La comprobación automática al arrancar consulta cada repositorio como mucho cada 6 horas. Las respuestas se cachean con ETag, así que no gastan cuota de la API.
- Sin token, GitHub permite 60 consultas por hora. Con **🔑GitHub** (un token *fine-grained* sin permisos basta) el límite sube a 5000.
- Al actualizar: comprueba que el juego no esté abierto, verifica el SHA-256 cuando el release lo publica y extrae `.zip` y `.tar.gz` de forma nativa (`.7z` y `.rar` necesitan 7-Zip instalado). Después escribe `version.txt` e `installed-build.txt` en el formato de Quiver. Los instaladores (`setup.exe`, `.msi`) se abren para que los completes tú.
- El auto-update viene **desactivado** por defecto en configuraciones nuevas.

## Datos

`%APPDATA%\RecompVault\`: `config.json`, `release_cache.json`, `covers\`, `system_logos\` y `recompvault.log`
(botón 📄). Si `config.json` se corrompe, se guarda una copia `config.json.bad-*` en vez de perderlo.

## Compilar

```
dotnet build -c Release
dotnet run --project tests/CoreTests       # pruebas del núcleo (funcionan también en Linux)
```

GitHub Actions compila en cada push y publica `RecompVault.exe` como artefacto (necesita
.NET 10 Desktop Runtime).
