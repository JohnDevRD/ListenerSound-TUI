# ListenerSound

Aplicación TUI (Terminal User Interface) para disparar eventos de audio desde múltiples PCs cliente hacia un servidor central vía TCP.

Ideal para presentaciones, eventos en vivo, teatro, podcasts, o cualquier escenario donde necesites reproducir audios desde distintas ubicaciones con solo presionar una tecla.

## Arquitectura

```
┌─────────────────────┐       TCP        ┌─────────────────────┐
│  PC Cliente 1       │ ──────────────▶  │                     │
│  Presiona F4        │                  │   PC Servidor       │
│                     │                  │                     │
│  PC Cliente 2       │ ──────────────▶  │   Reproduce audio   │
│  Presiona Space     │                  │   asignado a cada   │
│                     │                  │   cliente           │
│  PC Cliente 15      │ ──────────────▶  │                     │
│  Presiona Enter     │                  └─────────────────────┘
└─────────────────────┘
```

- **Servidor**: Escucha conexiones TCP, recibe triggers y reproduce los audios asignados a cada cliente mediante NAudio.
- **Cliente**: Se conecta al servidor, detecta la tecla configurada y envía el trigger.

## Requisitos

- Windows 10/11 (NAudio requiere Windows Media Foundation)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Altavoces o sistema de audio en el servidor

## Instalación

```bash
git clone https://github.com/tuusuario/ListenerSound.git
cd ListenerSound
dotnet build
```

## Uso

### Servidor

```bash
dotnet run -- server
```

### Cliente

```bash
dotnet run -- client
```

O ejecutar el binario compilado:

```bash
ListenerSound server
ListenerSound client
```

## Configuración

### Servidor (`server-config.json`)

Define el puerto, la carpeta de audios y qué audio reproduce cada cliente:

```json
{
  "Port": 5000,
  "AudioFolder": "C:\\Audios",
  "Clients": [
    {
      "Id": "PC1",
      "AudioFile": "bienvenida.wav",
      "Description": "Mensaje de bienvenida"
    },
    {
      "Id": "PC2",
      "AudioFile": "aplausos.mp3",
      "Description": "Aplausos"
    }
  ],
  "Schedules": [
    {
      "Id": "S1",
      "AudioFile": "campana.wav",
      "Description": "Campana cada hora",
      "IntervalValue": 1,
      "IntervalUnit": "horas",
      "Enabled": true
    }
  ]
}
```

### Cliente (`client-config.json`)

Cada PC cliente tiene su propio archivo de configuración:

```json
{
  "ServerIp": "192.168.1.100",
  "ServerPort": 5000,
  "ClientId": "PC1",
  "TriggerKey": "F4",
  "Description": "Mensaje de bienvenida"
}
```

> **Nota**: `client-config.json` está en `.gitignore` para que cada usuario tenga su configuración local sin subirla al repositorio.

### Editor de configuración integrado

Presioná **C** dentro de la TUI (tanto en servidor como cliente) para abrir un menú interactivo donde podés modificar toda la configuración sin editar archivos JSON manualmente.

## Teclas disponibles

Cualquier `ConsoleKey` de .NET: `F1`-`F24`, `A`-`Z`, `D0`-`D9`, `Space`, `Enter`, `Up`, `Down`, etc.

## Programación de audios

El servidor permite reproducir audios automáticamente cada cierto intervalo (segundos, minutos u horas). Configurable desde el menú `C`.

## Licencia

[MIT](LICENSE)
