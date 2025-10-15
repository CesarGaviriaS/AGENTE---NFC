using Microsoft.AspNetCore.SignalR.Client;
using PCSC;
using PCSC.Iso7816;
using PCSC.Monitoring;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions; // <-- AÑADIDO: Para usar expresiones regulares
using System.Threading.Tasks;

public enum AgentMode
{
    IDLE,
    CONTINUOUS_READ,
    AWAITING_TAG_FOR_WRITE,
    AWAITING_TAG_FOR_CLEAN
}

class Program
{
    private static ISCardContext? context;
    private static HubConnection? connection;
    private static AgentMode currentMode = AgentMode.CONTINUOUS_READ;
    private static string? dataToWrite;

    static async Task Main(string[] args)
    {
        Console.WriteLine("--- Agente NFC v9.1 (Análisis Inteligente) by GAVO ---");

        const string hubUrl = "https://localhost:44342/nfcHub";
        connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // --- Handlers de SignalR (sin cambios) ---
        connection.On<string>("RequestWriteMode", (data) =>
        {
            currentMode = AgentMode.AWAITING_TAG_FOR_WRITE;
            dataToWrite = data;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[MODO CAMBIADO] -> AWAITING_TAG_FOR_WRITE. Datos a escribir: '{data}'");
            Console.ResetColor();
            connection.InvokeAsync("SendStatusUpdate", "Agente listo. Acerque el tag que desea grabar.", "info");
        });

        connection.On("RequestCleanMode", () =>
        {
            currentMode = AgentMode.AWAITING_TAG_FOR_CLEAN;
            dataToWrite = null;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[MODO CAMBIADO] -> AWAITING_TAG_FOR_CLEAN");
            Console.ResetColor();
            connection.InvokeAsync("SendStatusUpdate", "Agente listo. Acerque el tag que desea limpiar.", "warning");
        });

        connection.On("RequestReadMode", () =>
        {
            currentMode = AgentMode.CONTINUOUS_READ;
            dataToWrite = null;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[MODO CAMBIADO] -> CONTINUOUS_READ");
            Console.ResetColor();
        });

        await ConnectToHub();

        try
        {
            context = ContextFactory.Instance.Establish(SCardScope.System);
            var readerNames = context.GetReaders();
            if (readerNames == null || readerNames.Length == 0)
            {
                Console.WriteLine("ERROR: No se encontraron lectores NFC.");
                Console.ReadKey();
                return;
            }

            Console.WriteLine($"Lector encontrado: {readerNames[0]}");
            var monitor = new SCardMonitor(ContextFactory.Instance, SCardScope.System);
            monitor.CardInserted += OnCardInserted;
            monitor.Start(readerNames);

            Console.WriteLine("\n--- Agente en modo de Lectura Continua por defecto ---");
            Console.WriteLine("Presione Enter para salir...");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al inicializar lector: {ex.Message}");
            Console.ReadKey();
        }
    }

    private static async void OnCardInserted(object? sender, CardStatusEventArgs args)
    {
        if (connection?.State != HubConnectionState.Connected) return;
        Console.WriteLine($"\n[Tag Detectado en modo: {currentMode}]");
        try
        {
            using var reader = new SCardReader(context);
            if (reader.Connect(args.ReaderName, SCardShareMode.Shared, SCardProtocol.Any) != SCardError.Success)
            {
                Console.WriteLine("No se pudo conectar al tag.");
                return;
            }
            switch (currentMode)
            {
                case AgentMode.CONTINUOUS_READ:
                    await HandleContinuousRead(reader);
                    break;
                case AgentMode.AWAITING_TAG_FOR_WRITE:
                    await HandleWrite(reader);
                    // El modo ahora se gestiona dentro de HandleWrite
                    break;
                case AgentMode.AWAITING_TAG_FOR_CLEAN:
                    await HandleClean(reader);
                    // El modo ahora se gestiona dentro de HandleClean
                    break;
                case AgentMode.IDLE:
                    Console.WriteLine("Tag detectado en modo IDLE. Ignorando.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error durante la operación con el tag: {ex.Message}");
            await connection.InvokeAsync("SendOperationFailure", $"Error en el agente: {ex.Message}");
        }
    }

    private static async Task HandleContinuousRead(ISCardReader reader)
    {
        string? contenido = LeerContenidoDelTag(reader); // Usa la nueva función inteligente
        if (!string.IsNullOrEmpty(contenido))
        {
            Console.WriteLine($"Contenido extraído: '{contenido}'. Transmitiendo...");
            await connection.InvokeAsync("TransmitirDatosTag", contenido);
        }
        else
        {
            Console.WriteLine("No se encontró un formato de datos válido (ej: '1,6') en el tag.");
        }
    }

    private static async Task HandleWrite(ISCardReader reader)
    {
        Console.WriteLine($"Intentando escribir: '{dataToWrite}'");
        bool success = EscribirContenidoEnTag(reader, dataToWrite);
        if (success)
        {
            Console.WriteLine("Escritura exitosa. Verificando...");
            string? verifiedData = LeerContenidoDelTag(reader); // Usa la nueva función inteligente
            if (verifiedData == dataToWrite)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Verificación exitosa.");
                Console.ResetColor();
                await connection.InvokeAsync("SendOperationSuccess", "Tag grabado y verificado con éxito.", verifiedData);

                // CAMBIO SOLICITADO: Volver a modo lectura continua
                currentMode = AgentMode.CONTINUOUS_READ;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n[MODO CAMBIADO] -> CONTINUOUS_READ");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fallo de verificación. Leído: '{verifiedData}' | Esperado: '{dataToWrite}'");
                Console.ResetColor();
                await connection.InvokeAsync("SendOperationFailure", "Error: La verificación falló. El tag no se grabó correctamente.");
                currentMode = AgentMode.IDLE; // Volver a inactivo en caso de fallo
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Fallo en la escritura.");
            Console.ResetColor();
            await connection.InvokeAsync("SendOperationFailure", "Error: No se pudo escribir en el tag.");
            currentMode = AgentMode.IDLE; // Volver a inactivo en caso de fallo
        }
    }

    private static async Task HandleClean(ISCardReader reader)
    {
        Console.WriteLine("Intentando limpiar el tag...");
        bool success = EscribirContenidoEnTag(reader, ""); // Escribir un string vacío
        if (success)
        {
            Console.WriteLine("Limpieza exitosa. Verificando...");
            string? verifiedData = LeerContenidoDelTag(reader);
            if (string.IsNullOrEmpty(verifiedData))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Verificación de limpieza exitosa.");
                Console.ResetColor();
                await connection.InvokeAsync("SendOperationSuccess", "Tag limpiado y verificado con éxito.", "");

                // CAMBIO SOLICITADO: Volver a modo lectura continua
                currentMode = AgentMode.CONTINUOUS_READ;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n[MODO CAMBIADO] -> CONTINUOUS_READ");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fallo de verificación. El tag no está vacío.");
                Console.ResetColor();
                await connection.InvokeAsync("SendOperationFailure", "Error: La verificación falló. El tag no se limpió correctamente.");
                currentMode = AgentMode.IDLE; // Volver a inactivo en caso de fallo
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Fallo en la limpieza.");
            Console.ResetColor();
            await connection.InvokeAsync("SendOperationFailure", "Error: No se pudo limpiar el tag.");
            currentMode = AgentMode.IDLE; // Volver a inactivo en caso de fallo
        }
    }

    // --- FUNCIÓN DE LECTURA ACTUALIZADA ---
    private static string? LeerContenidoDelTag(ISCardReader reader)
    {
        var allData = new List<byte>();
        for (byte page = 4; page <= 39; page++)
        {
            var apdu = new CommandApdu(IsoCase.Case2Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                Instruction = InstructionCode.ReadBinary,
                P1 = 0x00,
                P2 = page,
                Le = 4
            };
            var response = Transmit(reader, apdu.ToArray());
            if (response != null && response.SW1 == 0x90 && response.SW2 == 0x00)
            {
                allData.AddRange(response.GetData());
            }
            else
            {
                break; // Detener si una página falla
            }
        }

        if (allData.Count == 0) return null;

        // 1. Convierte todos los bytes a un string, ignorando errores de conversión.
        string rawText = Encoding.UTF8.GetString(allData.ToArray());

        // 2. Usa una expresión regular para encontrar el primer patrón "número,número".
        Match match = Regex.Match(rawText, @"\d+,\d+");

        // 3. Si se encuentra, devuelve ese valor limpio. Si no, devuelve null.
        return match.Success ? match.Value : null;
    }

    // --- FUNCIÓN DE ESCRITURA ACTUALIZADA ---
    private static bool EscribirContenidoEnTag(ISCardReader reader, string? text)
    {
        // Prepara un buffer del tamaño del área de usuario de un NTAG213 para asegurar que se sobrescriba todo.
        byte[] buffer = new byte[144];

        if (!string.IsNullOrEmpty(text))
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            if (textBytes.Length > buffer.Length)
            {
                Console.WriteLine("Error: El texto es demasiado largo para el tag.");
                return false;
            }
            // Copia los nuevos datos al principio del buffer. El resto quedará en ceros,
            // lo que limpiará efectivamente los datos antiguos del tag.
            Array.Copy(textBytes, buffer, textBytes.Length);
        }

        // Escribe el buffer completo página por página.
        for (byte page = 4; page < 36; page++) // Páginas 4 a 35 son de usuario
        {
            int index = (page - 4) * 4;
            byte[] pageData = new byte[4];
            Array.Copy(buffer, index, pageData, 0, 4);

            var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                Instruction = (InstructionCode)0xD6, // WRITE BINARY
                P1 = 0x00,
                P2 = page,
                Data = pageData
            };

            var response = Transmit(reader, apdu.ToArray());
            if (response == null || response.SW1 != 0x90)
            {
                Console.WriteLine($"Error escribiendo en la página {page}");
                return false;
            }
        }
        return true;
    }

    private static ResponseApdu? Transmit(ISCardReader reader, byte[] command)
    {
        var responseBuffer = new byte[256];
        var sc = reader.Transmit(SCardPCI.GetPci(reader.ActiveProtocol), command, ref responseBuffer);
        if (sc != SCardError.Success)
        {
            Console.WriteLine("Error de transmisión PC/SC: " + sc);
            return null;
        }
        return new ResponseApdu(responseBuffer, responseBuffer.Length, IsoCase.Case4Short, reader.ActiveProtocol);
    }

    private static async Task ConnectToHub()
    {
        try
        {
            await connection.StartAsync();
            Console.WriteLine("Conexión con el Hub establecida.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al conectar con el Hub: {ex.Message}. Reintentando en 5s...");
            await Task.Delay(5000);
            await ConnectToHub();
        }
    }
}

