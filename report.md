# Informe de prueba de foco en Xbox Mode

Fecha: 4 de noviembre de 2026

Sistema observado: Windows 11 25H2, build 26200.9711

Estado durante las pruebas: FSE activo; 2 rutas de pantalla activas; escritorio `Default`.

## Pregunta

¿Puede una ventana de aplicación normal en la pantalla secundaria permanecer visible y recibir clics y escritura mientras Xbox Full Screen Experience sigue activo?

## Resultado

La prueba no llegó a verificar entrada interactiva. Windows/DWM marcó la ventana de prueba como `DWMWA_CLOAKED=2` (cloak de shell) mientras FSE seguía activo. El intento puntual de quitar el cloak desde el mismo proceso tuvo HRESULT de éxito, pero no mantuvo la ventana disponible. Computer Use pudo leer el árbol de accesibilidad en una observación temprana; el clic falló porque no había geometría de entrada utilizable y después la ventana no admitió captura. No se registró ningún clic de ratón ni texto escrito por teclado en el control.

Esto demuestra una restricción observada de visibilidad/gestión de ventanas para esta ventana y esta build. No identifica todavía qué proceso o módulo reaplica el cloak, no demuestra una regla universal para todas las aplicaciones y no prueba que una solución sea imposible.

## Medidas

La captura final registra `supported=true`, `active=true`, `canSet=true`, dos monitores de 1920×1080 y dos rutas activas; la secundaria está en `(1920, 0)`. FSE permaneció activo en las muestras de las tres pruebas. El JSONL completo queda local porque contiene metadatos de la máquina.

Se hizo un intento externo desde el colector sobre el HWND de prueba. `DwmSetWindowAttribute(DWMWA_CLOAK=false)` devolvió `0x80070005` (acceso denegado); el ejecutable no era propietario de esa ventana.

Después se hicieron tres intentos de un solo uso desde la propia aplicación de prueba. Cada sesión tuvo un límite de 30 segundos y cerró automáticamente:

| PID | Observación |
|---:|---|
| A | La llamada del propietario tuvo HRESULT `0`. DWM registró `cloaked=0` y luego `cloaked=2` unos 20 segundos después, todavía con FSE activo. La interacción de UI no pudo completarse antes de perder la superficie de entrada. |
| B | La llamada tuvo HRESULT `0`, pero la primera muestra ya indicó `cloaked=2`; se mantuvo así hasta el límite de 30 segundos. |
| C | La llamada al mostrarse la ventana tuvo HRESULT `0`; 257 ms después DWM indicó `cloaked=2`, que siguió en las muestras posteriores. |

Los JSONL completos y el error de la prueba externa quedan en `data/`, excluidos del repositorio porque incluyen procesos y metadatos locales. La clasificación `DWMWA_CLOAKED=2` corresponde al cloak aplicado por la shell según la [documentación de Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute).

## Límites y seguridad

- No se desactivaron HVCI, VBS, Secure Boot ni otras protecciones.
- No se inyectó ningún DLL, no se alteró un binario del sistema y no se instaló un driver.
- No se probó ningún juego ni software con anti-cheat.
- No se completaron el ensayo de 30 minutos, los 20 cambios de foco ni la prueba de escritura. No hay un parche verificado.
- `DWMWA_CLOAKED=2` señala el tipo de cloak que expone DWM; estas muestras no atribuyen por sí solas qué componente tomó la decisión.

## Siguiente investigación útil

Capturar ETW de ventanas/DWM y correlacionarlo con los cambios de foco y el momento en que `cloaked` pasa a `2`, para atribuir la transición a un proceso y módulo concretos. Repetir el test de clic solo si una captura simultánea confirma que la ventana está realmente visible y que DWM no la ha vuelto a ocultar. Mantener cualquier experimento acotado y reversible.
