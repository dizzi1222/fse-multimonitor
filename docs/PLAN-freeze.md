# PLAN — Miniaturas congeladas con Riot/Sekiro (Xbox FSE) · fse-multimonitor

## Objetivo
Miniaturas en vivo con Riot/Sekiro al frente, o límite demostrado con evidencia.

## Contexto (verificado)
- FSE = Xbox Full Screen Experience (Win11 25H2). El shell pone DWMWA_CLOAKED=2 a ventanas ajenas: es normal.
- README/report.md ya probaron mirror en vivo con cloak=2 (FseTestWindow) → cloak=2 NO es la causa.
- Fallo: fuente `zen` (Gecko), cloaked=2, occluded=true, occluder="Riot Client"; se congela con Riot y Sekiro al frente.
- Ya hecho en v1.1.1 (no tocar ni regresionar): guard clauses en ThumbnailPreview.ApplyThumbnailLayout/CreateCells; layout manual escalado en WindowPicker (AutoScaleMode.None, _layoutScale, _baseLayout, ApplyScaledLayout).

## Hipótesis (se decide con datos en Fase 2)
- H1 (principal): la app fuente deja de pintar al quedar ocluida por una ventana opaca a pantalla completa (Chromium: native window occlusion; Firefox/Zen: widget.windows.window_occlusion_tracking). DWM está sano: no hay frames nuevos.
- H2: DWM/GPU deja de actualizar la superficie de la fuente mientras un juego tiene el foco (flip directo, Modo Juego, EcoQoS).
- H3: el overlay del monitor 2 se estanca o pierde z-order/propiedades tras el cambio de modo de pantalla del juego.

## Reglas
- Nada de inyección, hooks (Present/DirectX) ni OpenProcess con VM_READ/WRITE/OPERATION sobre procesos de juego (Vanguard/anti-cheat). Solo APIs de ventana y PROCESS_QUERY_LIMITED_INFORMATION.
- Sin admin, sin uncloak, todo reversible, UI en español, no loguear títulos de ventana.
- Prohibido migrar a WGC antes de la Fase 2.
- Un commit por fase. No declarar "arreglado" sin la matriz de la Fase 1 en verde.
- Al terminar cada fase: resumen de 5 líneas y esperar "continúa".

## Fase 0 — Leer código (sin cambios) → docs/freeze-notes.md
Responder con archivo:línea:
1. Dónde se llama DwmRegisterThumbnail/DwmUpdateThumbnailProperties y con qué flags.
2. Qué dispara re-registro o relayout (DpiChanged, cambio de pantalla, timers, ToggleFullScreen).
3. Cómo se calcula `occluded`/`occluder` en el log de estado (¿z-order?, ¿ignora toolwindow/cloaked?).
4. Cómo se mantiene el overlay encima (TopMost, SetWindowPos, WS_EX_*) y en qué hilo corre su UI.
5. Qué hacen `probe`/`restore` (transacción, HWND + inicio de proceso). Ojo: `probe` rechaza ventanas cloaked.
6. Si el log registra la evolución del cloak de la fuente (se vio alternar 0↔2).

## Fase 1 — Laboratorio: `FseMultiMonitor.exe freeze-lab`
Usa el ThumbnailPreview real. Debe poder iterarse sin lanzar juegos cada vez.
Fuentes (en monitor 1; repetir S2 en monitor 2), seleccionadas por título `FSE-LAB` (solo para elegir, sin loguearlo):
- S1 `FseTestWindow --animate` (WinForms, 30 fps, barra de color + contador). No consciente de oclusión: control.
- S2 Zen/Firefox con `lab/anim.html` (requestAnimationFrame + contador grande).
- S3 Edge/Chrome con la misma página.
Oclusores:
- F0 solo shell Xbox (línea base).
- F1 Riot Client · F2 Sekiro: el lab espera a que el primer plano sea ese proceso 3 s y arranca solo (en FSE el teclado va al juego, no hay "pulsa Enter").
- F3 `FseTestWindow --occluder`: opaca, sin bordes, pantalla completa en monitor 1, no toolwindow ni layered (imita un juego). Registrar su cloak: si sale cloaked no sirve de oclusor.
- F4 igual que F3 con WS_EX_TOOLWINDOW (control negativo).
Medición (15 s por celda, 10 Hz):
- thumbDistinct: hashes distintos de la región de cada miniatura capturada del monitor 2 (CopyFromScreen). LIVE si ≥10 en 10 s.
- overlayTick: cuadrito de 12 px que alterna color a 10 Hz en una esquina fuera de las celdas (solo modo lab): prueba que el overlay sigue vivo.
- srcCloak, srcOccluded, srcMonitor, occluderCloak, fgProcess (solo ProcessName).
- Entorno: build de Windows, GPU/driver, HDR/Auto-HDR, Modo Juego.
Salida: JSONL en %FSE_MM_DATA%\sessions + tabla en docs/FREEZE-LAB.md.

## Fase 2 — Veredicto (escribirlo en FREEZE-LAB.md)
| Resultado | Veredicto | Siguiente |
|---|---|---|
| S1 LIVE; S2/S3 FROZEN bajo F1/F2/F3 (F0 puede variar); F4 LIVE | H1 confirmada | 3A |
| S1 FROZEN también; overlayTick vivo | H2 | 3B |
| overlayTick se detiene | H3 | 3C |
| F3 no reproduce pero F1/F2 sí | el oclusor real difiere: comparar estilos (WS_EX_*, cloak) de Riot/Sekiro vs F3 con `capture`; no avanzar | — |
| Todo LIVE | no reproducible: pedir log y versiones | — |

## Fase 3A — H1 (esperado)
1. Confirmar por intervención y registrar cuál funciona, todas bajo F1/F2/F3:
   (a) Zen con widget.windows.window_occlusion_tracking.enabled=false;
   (b) Edge con --disable-features=CalculateNativeWinOcclusion;
   (c) fuente movida al monitor del overlay (mover de solo-lab con la transacción de restauración, porque `probe` rechaza cloaked).
2. `OcclusionAdvisor` (nuevo, src/): clasifica por ProcessName (Chromium/Electron: chrome, msedge, brave, vivaldi, opera, discord; Gecko: firefox, zen, librewolf, floorp) y combina con `occluded` → riesgo de pausa.
3. Overlay: insignia "⏸ Pausada por oclusión" en la miniatura afectada. Respetar el layout actual (CreateCells con guard).
4. Selector (WindowPicker con _baseLayout/ApplyScaledLayout): botón "¿Se congela?" → diálogo con solución copiable: Gecko → about:config, widget.windows.window_occlusion_tracking.enabled=false y reiniciar; Chromium → lanzar con --disable-features=CalculateNativeWinOcclusion y verificar en chrome://version (edge://version en Edge). No editar perfiles ni accesos directos.
5. Solo si 1(c) funcionó: opción opt-in "Anti-oclusión" (apagada por defecto) que mueve fuentes al monitor del overlay reutilizando la transacción de probe/restore (rect + HWND + inicio de proceso; SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE; saltar maximizadas/minimizadas; permitir cloaked porque en FSE todas lo están). Restaurar al cerrar la vista, al salir de FSE, con Ctrl+C y con `restore <id>` tras un crash.

## Fase 3B — H2
1. `wgc-probe` (solo diagnóstico): contar FrameArrived/s de la ventana fuente y del monitor 1 con S1 bajo F1/F2/F3. Revisar también Modo de eficiencia (EcoQoS) del proceso fuente.
2. Si WGC sigue entregando frames con la miniatura DWM congelada: interfaz `IFrameSource` (DwmThumbnailSource por defecto, WgcCaptureSource como respaldo automático ante estancamiento), validada en el lab.
3. Si WGC también se detiene: es un límite de Windows. Documentar en README ("Limitaciones conocidas") y cerrar. Sin hooks.

## Fase 3C — H3
Re-asegurar TOPMOST/NOACTIVATE y reaplicar DwmUpdateThumbnailProperties en WM_DISPLAYCHANGE, DpiChanged y cambio de foreground (debounce 250 ms). Re-registrar si algún HRESULT ≠ S_OK. Mover la UI del overlay a un hilo propio si el estancamiento viene del hilo.

## Fase 4 — Verificación y release
Aceptación (todas):
- S1 y S2/S3 LIVE 60 s bajo F1, F2, F3 y con Sekiro/Riot reales.
- Regresión: Pantalla completa ↔ Vista ajustada sin crash (incluido count=0); botón Actualizar visible en 96/120/144/192 DPI; 1–4 ventanas; cerrar/reabrir vista; salir de FSE cierra todo.
- Si hay mover-fuentes: matar el proceso a mitad de sesión y `restore <id>` deja todo como estaba.
- Tests mínimos: CreateCells(0,1,2,3,4) y OcclusionAdvisor.
Entregables: docs/FREEZE-LAB.md (tabla + veredicto), README (Limitaciones conocidas + sección de navegadores), CHANGELOG, versión v1.1.1 (advisor + docs) o v1.2.0 (si hay mover/WGC).

## Referencias
- https://github.com/chromium/chromium/blob/main/docs/windows_native_window_occlusion_tracking.md
- https://bugzilla.mozilla.org/show_bug.cgi?id=1802473
- https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
