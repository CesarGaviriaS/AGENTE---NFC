// --- Agente NFC (actualizado Wilmar) ---
using Microsoft.AspNetCore.SignalR.Client;
using PCSC;
using PCSC.Iso7816;
using PCSC.Monitoring;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        Console.WriteLine("--- Agente NFC v10.1 ---");

        const string hubUrl = "http://localhost:5075/nfcHub";
        connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Eventos desde SignalR
        connection.On<string>("RequestWriteMode", (data) =>
        {
            currentMode = AgentMode.AWAITING_TAG_FOR_WRITE;
            dataToWrite = data;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[MODO CAMBIADO] -> WRITE. Datos: '{data}'");
            Console.ResetColor();
        });

        connection.On("RequestCleanMode", () =>
        {
            currentMode = AgentMode.AWAITING_TAG_FOR_CLEAN;
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n[MODO CAMBIADO] -> CLEAN");
            Console.ResetColor();
        });

        connection.On("RequestReadMode", () =>
        {
            currentMode = AgentMode.CONTINUOUS_READ;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[MODO CAMBIADO] -> READ");
            Console.ResetColor();
        });

        await ConnectToHub();

        // Inicializar lector
        context = ContextFactory.Instance.Establish(SCardScope.System);
        var readers = context.GetReaders();
        if (readers == null || readers.Length == 0)
        {
            Console.WriteLine("❌ No se encontraron lectores NFC.");
            return;
        }

        Console.WriteLine($"📶 Lector detectado: {readers[0]}");
        var monitor = new SCardMonitor(ContextFactory.Instance, SCardScope.System);
        monitor.CardInserted += OnCardInserted;
        monitor.Start(readers);

        Console.WriteLine("\nEsperando tags... (ENTER para salir)");
        Console.ReadLine();
    }

    private static async void OnCardInserted(object? sender, CardStatusEventArgs e)
    {
        if (connection?.State != HubConnectionState.Connected) return;

        using var reader = new SCardReader(context);
        if (reader.Connect(e.ReaderName, SCardShareMode.Shared, SCardProtocol.Any) != SCardError.Success)
        {
            Console.WriteLine("No se pudo conectar al tag.");
            return;
        }

        switch (currentMode)
        {
            case AgentMode.CONTINUOUS_READ:
                await HandleRead(reader);
                break;
            case AgentMode.AWAITING_TAG_FOR_WRITE:
                await HandleWrite(reader);
                break;
            case AgentMode.AWAITING_TAG_FOR_CLEAN:
                await HandleClean(reader);
                break;
        }
    }

    // === LECTURA ===
    private static async Task HandleRead(ISCardReader reader)
    {
        string? contenido = LeerContenidoDelTag(reader);
        if (!string.IsNullOrEmpty(contenido))
        {
            Console.WriteLine($"🟢 Tag leído: {contenido}");
            await connection.InvokeAsync("ProcesarLecturaTag", contenido);
            await connection.InvokeAsync("SendTagEvent", "TagLeido", $"Tag leído: {contenido}");
        }
        else
        {
            await connection.InvokeAsync("SendTagEvent", "TagLeido", "⚠️ Tag vacío o formato inválido (esperado: '1,6').");
        }
    }

    // === ESCRITURA ===
    private static async Task HandleWrite(ISCardReader reader)
    {
        Console.WriteLine($"✏️ Escribiendo: {dataToWrite}");
        bool success = EscribirContenidoEnTag(reader, dataToWrite);
        if (!success)
        {
            Console.WriteLine("❌ Error al escribir el tag.");
            await connection.InvokeAsync("SendTagEvent", "TagGrabado", "❌ Error al escribir el tag.");
            return;
        }

        string? verifiedData = LeerContenidoDelTag(reader);
        if (verifiedData == dataToWrite)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ Tag grabado y verificado con éxito.");
            Console.ResetColor();

            await RegistrarTagEnBackend(verifiedData);
            await connection.InvokeAsync("SendTagEvent", "TagGrabado", $"✅ Tag grabado y verificado: {verifiedData}");
            await connection.InvokeAsync("SendOperationSuccess", "Tag grabado correctamente.", verifiedData);

            currentMode = AgentMode.CONTINUOUS_READ;
            Console.WriteLine("🔁 Volviendo a modo lectura.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Verificación fallida. Esperado: '{dataToWrite}', leído: '{verifiedData}'");
            Console.ResetColor();
            await connection.InvokeAsync("SendTagEvent", "TagGrabado", "❌ Verificación fallida.");
        }
    }

    // === LIMPIAR ===
    private static async Task HandleClean(ISCardReader reader)
    {
        Console.WriteLine("🧹 Limpiando tag...");
        
        // ✅ PRIMERO: Leer el código del tag antes de limpiarlo
        string? tagCode = LeerContenidoDelTag(reader);
        
        bool success = EscribirContenidoEnTag(reader, "");
        if (success)
        {
            Console.WriteLine("✅ Tag limpiado físicamente.");
            await connection.InvokeAsync("SendTagEvent", "TagLimpiado", "✅ Tag limpiado correctamente.");
            await connection.InvokeAsync("SendOperationSuccess", "Tag limpiado.", "");
            
            // ✅ NUEVO: Notificar al backend con el código para eliminar de BD
            if (!string.IsNullOrEmpty(tagCode))
            {
                Console.WriteLine($"📡 Enviando código del tag limpiado: {tagCode}");
                await connection.InvokeAsync("SendTagCleanedSuccess", tagCode);
            }
            
            currentMode = AgentMode.CONTINUOUS_READ;
        }
        else
        {
            Console.WriteLine("❌ Error al limpiar tag.");
            await connection.InvokeAsync("SendTagEvent", "TagLimpiado", "❌ Error al limpiar tag.");
        }
    }

    // === REGISTRO EN BACKEND ===
    private static async Task RegistrarTagEnBackend(string codigo)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("https://localhost:7280/") };

            var partes = codigo.Split(',');
            if (partes.Length != 2) return;

            string tipoPersona = partes[0] == "1" ? "Aprendiz" : "Usuario";
            int idPersona = int.Parse(partes[1]);

            var body = new { CodigoTag = codigo, IdPersona = idPersona, TipoPersona = tipoPersona };
            var json = JsonSerializer.Serialize(body);

            var response = await client.PostAsync("api/TagAsignado",
                new StringContent(json, Encoding.UTF8, "application/json"));

            string respuesta = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                Console.WriteLine($"🟢 Tag registrado en backend: {respuesta}");
            else
                Console.WriteLine($"⚠️ Falló el registro del tag: {respuesta}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error registrando tag: {ex.Message}");
        }
    }

    // === UTILIDADES ===
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
            var resp = Transmit(reader, apdu.ToArray());
            if (resp != null && resp.SW1 == 0x90) allData.AddRange(resp.GetData());
            else break;
        }
        string text = Encoding.UTF8.GetString(allData.ToArray());
        var match = Regex.Match(text, @"\d+,\d+");
        return match.Success ? match.Value : null;
    }

    private static bool EscribirContenidoEnTag(ISCardReader reader, string? text)
    {
        byte[] buffer = new byte[144];
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Array.Copy(bytes, buffer, bytes.Length);
        }

        for (byte page = 4; page < 36; page++)
        {
            int idx = (page - 4) * 4;
            var data = new byte[4];
            Array.Copy(buffer, idx, data, 0, 4);

            var apdu = new CommandApdu(IsoCase.Case3Short, reader.ActiveProtocol)
            {
                CLA = 0xFF,
                Instruction = (InstructionCode)0xD6,
                P1 = 0x00,
                P2 = page,
                Data = data
            };

            var resp = Transmit(reader, apdu.ToArray());
            if (resp == null || resp.SW1 != 0x90)
            {
                Console.WriteLine($"Error escribiendo página {page}");
                return false;
            }
        }
        return true;
    }

    private static ResponseApdu? Transmit(ISCardReader reader, byte[] command)
    {
        var responseBuffer = new byte[256];
        var sc = reader.Transmit(SCardPCI.GetPci(reader.ActiveProtocol), command, ref responseBuffer);
        return sc == SCardError.Success ? new ResponseApdu(responseBuffer, responseBuffer.Length, IsoCase.Case4Short, reader.ActiveProtocol) : null;
    }

    private static async Task ConnectToHub()
    {
        int intentos = 0;
        const int maxIntentos = 5;
        
        while (intentos < maxIntentos)
        {
            try
            {
                intentos++;
                Console.WriteLine($"🔄 Intentando conectar al Hub NFC... (Intento {intentos}/{maxIntentos})");
                
                await connection.StartAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✅ Conectado al Hub NFC exitosamente.");
                Console.ResetColor();
                return;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ Error al conectar (Intento {intentos}/{maxIntentos}): {ex.Message}");
                Console.ResetColor();
                
                if (intentos < maxIntentos)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("⏳ Reintentando en 3 segundos...");
                    Console.ResetColor();
                    await Task.Delay(3000);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n⚠️ ============================================");
                    Console.WriteLine("⚠️  NO SE PUDO CONECTAR A LA API");
                    Console.WriteLine("⚠️ ============================================");
                    Console.WriteLine("⚠️  Asegúrate de que la API esté ejecutándose en:");
                    Console.WriteLine("⚠️  http://localhost:5075");
                    Console.WriteLine("⚠️ ");
                    Console.WriteLine("⚠️  Para iniciar la API, ejecuta:");
                    Console.WriteLine("⚠️  cd API---NFC-copy/API_NFC");
                    Console.WriteLine("⚠️  dotnet run");
                    Console.WriteLine("⚠️ ============================================\n");
                    Console.ResetColor();
                    
                    // Esperar y reintentar automáticamente
                    Console.WriteLine("🔄 Esperando 10 segundos antes de reintentar...");
                    await Task.Delay(10000);
                    intentos = 0; // Reiniciar contador para continuar intentando
                }
            }
        }
    }
}
