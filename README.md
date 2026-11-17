# RecompVault

Launcher para Windows (WPF, .NET 10) de recomps, decomps y ports. Se integra con
[Quiver Launcher](https://github.com/tgeorgiadis/quiver-launcher) para sacar de ahí los
nombres, los iconos, los repositorios y las actualizaciones.

## Interfaz

Estilo frontend de emulación: carrusel de sistemas con sus colores de marca y el logo en blanco,
cabecera del sistema elegido (logo grande, fabricante, año, número de juegos y actualizaciones) y
cuadrícula de portadas virtualizada (solo se dibuja lo visible, así va fluido con cientos de juegos).

- **Teclado:** flechas para moverse, Intro para jugar, RePág/AvPág para cambiar de sistema, Ctrl+F para buscar y F11 para el modo sofá.
- **Mando XInput:** cruceta o stick para moverse, A jugar, Y favorito, X detección, LB/RB sistema, B limpiar la búsqueda y Start para el modo sofá. Solo responde con RecompVault en primer plano.
- **Modo sofá:** pantalla completa ampliada y con la interfaz simplificada.
- **Logos propios:** pon `<SISTEMA>.png` en `%APPDATA%\RecompVault\system_logos_custom` (mejor con fondo transparente, para que se pinte en blanco).

## Explorar Quiver (✦)

Muestra el catálogo público de quiverlauncher.com, el mismo de la pestaña Explorar de Quiver.
Se puede ordenar por actualizados, nuevos, mejor valorados o nombre, buscar, filtrar por consola y
ocultar lo que ya tienes. Cada app enseña su arte, versiones (última y verificada), descripción y si
ya la tienes instalada. **Instalar** propone el sistema y la carpeta, deja elegir versión y paquete, e
instala con copia de deshacer y vínculo para futuras actualizaciones.

## Más

- **Tiempo de juego:** se mide mientras el juego está abierto. El "último jugado" se comparte con Quiver a través de `LastPlayed.txt`.
- **Argumentos de lanzamiento y ejecutar como administrador:** por juego, en ℹ.
- **Acceso directo en el escritorio** y **Añadir a Steam** como juego no-Steam, con la portada en la biblioteca. Steam tiene que estar cerrado; se guarda una copia de `shortcuts.vdf`.
- **Revisar detección (🔍):** tabla con todos los juegos y sus avisos (vínculo aproximado, sin vincular, versión desconocida, actualizados por la versión antigua...). Permite confirmar o desvincular en bloque y exportar a CSV.
- **Ficheros protegidos:** patrones globales (Ajustes) y por juego (ℹ) que nunca se sobrescriben al actualizar.
- **Ajustes (⚙)** en una sola ventana. **Copias de deshacer (↶)** con espacio ocupado y borrado.
- **Releases propios:** al etiquetar `vX.Y.Z` se publica el exe, y RecompVault avisa cuando hay una versión más nueva.

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

## Detección (botón ℹ)

Cada tarjeta tiene un botón **ℹ** (también en el clic derecho) que muestra qué se ha detectado y
de dónde sale cada dato: nombre, ejecutable (con la puntuación de cada candidato), app de Quiver
vinculada y tipo de coincidencia (ruta exacta, subcarpeta, nombre de carpeta o aproximada con su
puntuación), repositorio, filtro de paquete, versiones y portada. Desde ahí puedes cambiar:

- el nombre (vacío = automático);
- el ejecutable (lista de candidatos o "Examinar...");
- la app de Quiver (lista filtrable), el repositorio (`propietario/repo` o URL), GitHub/GitLab y el filtro de paquete;
- "sin vincular" para que no se busquen actualizaciones de ese juego.

"Restablecer todo a automático" borra lo elegido a mano para esa carpeta.

## Verificación por huella (🔬)

Para descartar falsas detecciones, RecompVault puede confirmar el vínculo comparando lo que tienes instalado con los releases del repositorio:

1. **SHA-256 del paquete**: si Quiver dejó `installed-build.txt`, se compara con el `digest` que GitHub publica para cada asset.
2. **CRC32 + tamaño del ejecutable**: si no hay hash, se lee solo el directorio central de cada `.zip` del release (peticiones HTTP Range, unos KB por archivo, sin descargar el paquete) y se busca el `.exe` de lanzamiento con el mismo CRC32 y tamaño.

Se revisan hasta 25 releases por repositorio (incluidos los candidatos alternativos si el vínculo actual no coincide). Si hay coincidencia, el vínculo queda marcado como **Verificado por huella** y la versión instalada se fija a ese tag. Si no coincide con nada, se marca **✖ huella** para revisarlo a mano. Requiere que el repositorio conserve releases antiguos.

Dónde: menú contextual de la carátula, botón **🔬 Guardar y verificar con GitHub** en ℹ, y **🔬 Verificar seleccionados** en 🔍 Revisar (con filtros "Sin verificar" y "Huella no coincide" y columna *Huella*). Con muchos juegos conviene un token de GitHub en Ajustes.

## Deshacer actualización

Antes de escribir cada fichero, la actualización mueve el original a
`<juego>\.recompvault-undo\<fecha>\` (misma unidad: instantáneo). Si la actualización falla a
medias se revierte sola. Si termina pero el juego no era el correcto, **↶** (en la tarjeta o en el
clic derecho) restaura los ficheros reemplazados, borra los que añadió y devuelve `version.txt`.
Se guarda una copia por juego (`UndoBackupsToKeep` en `config.json`). Las carpetas que actualizó la
versión anterior de RecompVault no tienen copia; la ventana ℹ lo indica.

## Instalar una versión concreta

Clic derecho → **⇩ Instalar una versión concreta...** (o "Guardar e instalar versión..." en ℹ).
Lista los releases del repositorio vinculado con fecha, prereleases, notas y paquetes. Recomienda
el paquete según el filtro de Quiver, el paquete instalado y la plataforma, y deja elegir otro.
Sirve para reinstalar un juego que quedó mal, o para volver a una versión anterior. Con
**"Fijar esta versión"** el juego no se marca como desactualizado ni se auto-actualiza (📌) hasta
que actualices a mano con ⟳. Como cualquier actualización, se puede deshacer con ↶.

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
