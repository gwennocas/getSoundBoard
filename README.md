# Get SoundBoard

Soundboard open source para Windows 10/11. Toque MP3 e WAV por botões ou atalhos globais. O app envia sua voz e os sons para um microfone virtual e permite ouvir os sons em volume separado nos seus alto-falantes.

## Como usar

1. Instale o [VB-CABLE pelo site oficial](https://vb-audio.com/Cable/index.htm) e reinicie o Windows conforme as instruções dele. O driver não acompanha o instalador deste projeto.
2. Abra o Get SoundBoard. Escolha seu microfone físico, seus alto-falantes/fone e **CABLE Input**. Clique em **Aplicar**.
3. No Discord ou jogo, escolha **CABLE Output** como dispositivo de entrada. O app mistura sua voz física com os sons nesse dispositivo.
4. Adicione MP3 ou WAV pelo botão ou arrastando arquivos para a janela. Clique em **Tocar** ou configure um atalho em **Editar**.

Os dois controles de volume afetam os sons do soundboard; o volume da sua voz vem das configurações do microfone no Windows. Ao iniciar um novo som, o anterior para. **Parar tudo** interrompe o som, mantendo sua voz no chat.

O atalho global para parar é **Ctrl+Alt+Backspace**. Cada som pode receber um atalho com `Ctrl+Alt`, `Ctrl+Shift` ou `Alt+Shift` e uma letra, número ou tecla F1–F12.

Se um dispositivo for desconectado, escolha os dispositivos novamente e clique em **Aplicar**. Se um atalho já estiver em uso por outro programa, o app informa o conflito. Alguns jogos com captura exclusiva de teclado podem impedir atalhos globais; os botões da janela continuam disponíveis.

## Compilar

Requer Windows x64, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) e as ferramentas de desenvolvimento WinUI/Windows App SDK do Visual Studio. O projeto usa C# com WinUI 3 e NAudio.

```powershell
dotnet restore GetSoundBoard.csproj
dotnet build GetSoundBoard.csproj -c Release -p:Platform=x64
dotnet publish GetSoundBoard.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
```

O workflow em `.github/workflows/build.yml` compila a versão Windows e gera o instalador com Inno Setup. Uma tag `v*` anexa o instalador ao GitHub Release. O instalador inicial não é assinado; o Windows pode mostrar um aviso de editor desconhecido.

## Dados e licença

Os arquivos importados e as configurações ficam em `%LOCALAPPDATA%\GetSoundBoard`. O app não usa conta nem serviço online. Código sob licença [MIT](LICENSE). VB-CABLE e NAudio mantêm suas próprias licenças.
