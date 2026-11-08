# Fase 0 — Notas de lectura de código (sin cambios)

Fecha: 2026-11-08
Repo: `C:\Users\Administrator\workspace\fse-multimonitor`
Base: `e1fccb2` + cambios locales v1.1.1 (`src/ThumbnailPreview.cs`, `src/WindowPicker.cs`)

> Objetivo de esta fase: confirmar los supuestos del plan leyendo el código real. No se modificó nada.

---

## 1. Dónde se llama DwmRegisterThumbnail / DwmUpdateThumbnailProperties y con qué flags

### Overlay (`WindowThumbnailPreview`)
- `src/ThumbnailPreview.cs:233` — `DwmRegisterThumbnail(Handle, hwnd, out thumbnail)` dentro de `RegisterThumbnails`, una vez por fuente al abrir.
- `src/ThumbnailPreview.cs:259` — `DwmUpdateThumbnailProperties` inmediatamente tras registrar (propiedades iniciales).
- `src/ThumbnailPreview.cs:418` — `DwmUpdateThumbnailProperties` en `ApplyThumbnailLayout` (relayout: DPI y toggle de pantalla completa).
- Declaraciones P/Invoke: `src/ThumbnailPreview.cs:39-49` (`dwmapi.dll`, `PreserveSig = true`).

Flags usados (constantes en `src/ThumbnailPreview.cs:15-18`):
```
DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly
```
- `Opacity = 255`, `Visible = true`, `SourceClientAreaOnly = true`.
- **No se envía rectángulo de origen** (no se usa `DwmTnpRectSource`): DWM escala la superficie completa del cliente.
- `Destination` se calcula con `Fit()` (`src/ThumbnailPreview.cs:424-432`) para preservar aspecto dentro de la celda.

### Selector (`WindowPicker`)
- `src/WindowPicker.cs:390` — `DwmRegisterThumbnail` por tarjeta visible (máx. 6 por página).
- `src/WindowPicker.cs:408` — `DwmUpdateThumbnailProperties` con los mismos flags (constantes `src/WindowPicker.cs:13-16`).
- Declaraciones P/Invoke: `src/WindowPicker.cs:40-50`.

---

## 2. Qué dispara re-registro o relayout

### Overlay
- `Shown` → `RegisterThumbnails(monitorNumber)` (`src/ThumbnailPreview.cs:167-177`). Único registro inicial.
- `DpiChanged` → `ApplyThumbnailLayout()` (`src/ThumbnailPreview.cs:178-183`).
- Botón "Pantalla completa"/"Vista ajustada" → `ToggleFullScreen()` → `ApplyThumbnailLayout()` (`src/ThumbnailPreview.cs:388-401`, llamada en 393).
- `_timer` de 1000 ms (`src/ThumbnailPreview.cs:61,184`) **solo muestrea estado** (`SampleState`); **no re-registra ni reaplica propiedades**.
- **No hay** manejo de `WM_DISPLAYCHANGE`, ni hook de primer plano, ni re-registro periódico. Tampoco se re-asegura TOPMOST después de crear la ventana.

### Selector
- `Shown` → `RefreshWindows()` + arranque de `_fseTimer` y `_centerTimer` (`src/WindowPicker.cs:206-217`).
- `_centerTimer` (400 ms, one-shot) → `CenterOnSelectedMonitor()` + `LayoutPreview()` + `UpdatePreviewGrid()` (`src/WindowPicker.cs:218-224`).
- `DpiChanged` → recalcula `_layoutScale` y `ApplyScaledLayout()` (`src/WindowPicker.cs:225-233`). No re-registra miniaturas directamente (salvo lo que haga el próximo `UpdatePreviewGrid`).
- `UpdatePreviewGrid()` se llama en: `RefreshWindows` (337), cambio de página por selección (`OnWindowSelected`, 353-358), `ChangePage` (425).
- `_fseTimer` (1000 ms) solo comprueba FSE y cierra tras 3 ticks inactivos (`src/WindowPicker.cs:234-243`). **No refresca miniaturas**.
- Conclusión: **no existe re-registro periódico** en ninguno de los dos formularios. Las miniaturas se registran una vez y DWM debe actualizarlas por su cuenta.

---

## 3. Cómo se calcula `occluded` / `occluder` en el log de estado

**No existe.** No hay ninguna ocurrencia de `occluded`, `occluder` ni lógica de oclusión en `src/` (búsqueda exhaustiva sin resultados).

El registro `state` real (`src/ThumbnailPreview.cs:324-332`) solo contiene:
```json
{"event":"state","fseActive":true,"overlayCloaked":0,
 "sources":[{"Hwnd":"...","ProcessName":"zen","ProcessId":14652,"alive":true,"cloaked":0}],
 "monitorNumber":2}
```
- `alive` = hilo válido + PID actual == PID original + `ProcessStartMatches` (`src/ThumbnailPreview.cs:310-313,346-355`).
- `cloaked` = `DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED)` (`src/ThumbnailPreview.cs:311`, atributo `14` en `:14`).
- **No se evalúa z-order, ni ventanas que tapen, ni si el oclusor es toolwindow/cloaked.**
- `Inspector` sí enumera en orden Z (`ZOrder = windows.Count`, `src/Inspector.cs:307`) y captura el primer plano (`src/Inspector.cs:275-285`), pero no deriva oclusión.

> **Hallazgo importante:** la línea de log citada en el reporte de sesión anterior
> (`"foreground":"Riot Client","occluded":true,"occluder":"Riot Client"`) **no la produce este código**.
> Esa evidencia no existe en el repo ni en los JSONL actuales. H1 sigue siendo hipótesis, no hecho verificado.

---

## 4. Cómo se mantiene el overlay encima y en qué hilo corre su UI

- `TopMost = true` (`src/ThumbnailPreview.cs:87`), `FormBorderStyle.None` (85), `ShowInTaskbar = false` (86).
- `ShowWithoutActivation => true` (`src/ThumbnailPreview.cs:188`).
- `CreateParams.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE` (`src/ThumbnailPreview.cs:190-198`; constantes 11-13).
- Posición/tamaño = bounds del monitor elegido, fijados en el constructor (`src/ThumbnailPreview.cs:82-90`). `AutoScaleMode.None` (93).
- **No hay `SetWindowPos` ni re-aseguramiento de z-order tras la creación.** Solo se confía en `TopMost`.
- **Hilo:** toda la UI corre en el hilo principal STA vía `Application.Run(form)` (`src/Program.cs:224-239`). No hay hilo dedicado para el overlay.
- El selector también es `TopMost` + `WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE` (`src/WindowPicker.cs:252-262`).

---

## 5. Qué hacen `probe` / `restore`

### `probe <HWND> [segundos 1-30] [--include-titles]` — `src/Program.cs:283-369`
1. Exige FSE activo y **≥2 rutas de pantalla** activas (`:298-301`).
2. Busca el HWND en el snapshot y exige `Visible && !Iconic && Cloaked == 0` (`:302-305`).
   → **Rechaza ventanas cloaked**, que es el estado normal bajo Xbox FSE.
3. Guarda rectángulo original (`:306-307`).
4. Calcula posición centrada en el monitor secundario (`:309-312`).
5. Exige `ProcessStartUtc` para recuperación segura (`:313-314`).
6. Crea y persiste `ProbeTransaction` con: `Id` (GUID), `Hwnd`, `ProcessId`, `ProcessStartUtc`, `Left/Top/Width/Height`, `Restored=false`, `CreatedUtc`, `Kind="position"` (`:315-318`, record en `src/Store.cs:6-8`).
7. Mueve con `SetWindowPos(..., x, y, originalRect.Width, originalRect.Height, SWP_NOZORDER | SWP_NOACTIVATE)` (`:327-328`) — **sin cambiar tamaño ni activar**.
8. Bucle de muestreo cada 500 ms con `Inspector.Capture`; registra cambios de firma y aborta si FSE se desactiva o la ventana se oculta (`:333-357`).
9. `finally`: restaura siempre (`:360-368`), también con Ctrl+C (`:323-324`).

### `restore <transaction-id>` — `src/Program.cs:371-433`
- Valida ID GUID (`N`) (`Store.ReadTransaction`, `src/Store.cs:61-68`).
- Verifica que el proceso original sigue vivo y con **el mismo `StartTime`** (`:385-396`).
- Verifica que el HWND actual pertenece al mismo PID y sigue teniendo rect (`:402-408`).
- Si `Kind == "cloak"`: limpia `DWMWA_CLOAK` con `DwmSetWindowAttribute` (`:409-421`).
- Si no: `SetWindowPos` a la posición/tamaño originales con `SWP_NOZORDER | SWP_NOACTIVATE` (`:422-424`).
- Marca `Restored=true` (`:430`, `Store.MarkRestored` `src/Store.cs:70-78`).

> Para la Fase 3A.5 (mover fuentes al monitor del overlay) habría que **permitir cloaked** en una variante del mover, reutilizando esta transacción (`Kind="position"` ya es suficiente para restaurar rectángulo).

---

## 6. ¿El log registra la evolución del cloak de la fuente?

- **Sí, parcialmente.** `SampleState` serializa el estado y solo escribe cuando cambia la firma (`src/ThumbnailPreview.cs:324-328`), a 1 Hz.
- Cada entrada `state` incluye `cloaked` por fuente (`:314-321`).
- En los JSONL reales se observa la alternancia `0 → 2 → 0 → 2` con FSE activo:
  `%LOCALAPPDATA%\FseMultiMonitor\sessions\preview-20261108-012205-823.jsonl`.
- **No** se registra quién ocluye, ni foreground, ni geometría de solape en el log `state`.
- El foreground solo aparece en `probe-sample` como `foreground={ForegroundHwnd}` (`src/Program.cs:343-345`) y en el snapshot completo (`Inspector.Snapshot.ForegroundProcess`, `src/Inspector.cs:39,68-71`).

---

## Implicaciones directas para la Fase 1 (`freeze-lab`)

1. **No hay infraestructura de oclusión que medir**: el lab debe calcular `srcOccluded`/`occluder` desde cero (z-order + rects + estilos), o limitarse a `fgProcess` + `cloaked`.
2. **No hay re-registro periódico**: si H3 (estancamiento del overlay) es cierto, no hay mecanismo actual que lo recupere. El lab necesita el `overlayTick` para distinguirlo de H1/H2.
3. **El overlay exige FSE activo y se autocierra** si `overlayCloaked != 0` o FSE cae (`src/ThumbnailPreview.cs:334-338`). El lab debe respetar esa condición o usar una variante de laboratorio.
4. **`probe` rechaza cloak != 0** (`src/Program.cs:304-305`): el mover-fuentes de 3A.5 necesita su propia ruta que permita cloaked.
5. **Punto de entrada CLI** para `freeze-lab`: `src/Program.cs:46-62` (switch de comandos).
6. **La evidencia citada de `occluded/occluder` no existe**; la Fase 1 debe producirla o descartar H1 con datos propios.

---

## Respuesta corta a "¿es problema del navegador?"

Todavía **no está probado**. Lo que el código confirma es:
- DWM recibe un registro válido (`registerResult=0`, `updateResult=0`) y el overlay vive (`overlayCloaked=0`).
- La fuente alterna `cloak=0/2` y, según `report.md`, con `cloak=2` la miniatura sí se vio en vivo en la prueba con `FseTestWindow`.
- Por tanto `cloak=2` **no** es la causa por sí solo.
- Que se congele con Zen/Riot/Sekiro y no con la ventana de prueba es **compatible** con la pausa de render por oclusión del navegador (H1), pero falta el control experimental (S1 vs S2/S3 bajo F1/F2/F3) que define la Fase 1.
