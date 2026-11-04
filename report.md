# Resultado: vista visual en el segundo monitor

Fecha: 4 de noviembre de 2026
Sistema observado: Windows 11 25H2, build 26200.9711
Estado observado: Xbox Full Screen Experience activo; dos rutas de pantalla activas.

## Resultado

El formulario de prueba se ve dentro del panel del segundo monitor mientras FSE permanece activo. El usuario confirmó primero la prueba DWM y luego la versión integrada. La aplicación normal puede seguir teniendo `DWMWA_CLOAKED=2`; la vista del panel es una miniatura viva administrada por DWM.

La implementación incluye un selector gráfico: marca de una a cuatro ventanas, elige el monitor de destino y abre el panel. El botón **Pantalla completa** extiende las miniaturas por toda la pantalla; **Vista ajustada** vuelve al diseño con márgenes. Al cerrar la vista se regresa al selector. El selector y el panel terminan al salir de FSE.

## Evidencia medida

- FSE permaneció activo y Windows reportó dos rutas de pantalla de 1920×1080.
- La ventana fuente `FseTestWindow` se registró con `sourceCloak=2`.
- El overlay quedó con `overlayCloaked=0`.
- `DwmRegisterThumbnail` y `DwmUpdateThumbnailProperties` devolvieron HRESULT `0`.
- En una muestra posterior el cloak de la ventana fuente alternó entre `0` y `2`; el estado del original depende de la shell. El contenido del formulario se mostró en el panel.

## Límites

- La miniatura es visual; no transmite clics, teclado ni interacción a la ventana original.
- Esto no desactiva el cloak de la ventana fuente ni es un parche de Explorer/Game Bar.
- No se verificó captura de superficies protegidas o seguras, compatibilidad con anti-cheat ni funcionamiento en otras builds y configuraciones.
- No se desactivaron HVCI, VBS ni Secure Boot, no se inyectó DLL ni se modificaron binarios del sistema.

Los registros JSONL locales se encuentran en `data\sessions` y están excluidos del repositorio porque incluyen identificadores y metadatos locales.
