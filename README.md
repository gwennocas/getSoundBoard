# getSoundBoard

Soundboard open source para Windows 10/11. Toque MP3 e WAV por botões ou atalhos globais. O app envia sua voz e os sons para um microfone virtual e permite ouvir os sons em volume separado nos seus alto-falantes.

## Como usar

1. No app, clique em **Baixar ZIP oficial do VB-Audio**. O download também está disponível na [página oficial do VB-CABLE](https://vb-audio.com/Cable/index.htm). O driver não acompanha o instalador do getSoundBoard.
2. Abra o ZIP baixado e extraia todos os arquivos para uma pasta.
3. Na pasta extraída, clique com o botão direito no instalador de 64 bits (x64), escolha **Executar como administrador** e permita a instalação do driver. O instalador oficial pode pedir confirmação do Windows.
4. Reinicie o Windows para o novo dispositivo de áudio aparecer.
5. Abra o getSoundBoard, clique em **Atualizar dispositivos** e escolha seu microfone físico e alto-falantes/fone. Se encontrado, o app seleciona **CABLE Input** automaticamente. Clique em **Aplicar**.
6. No Discord ou jogo, escolha **CABLE Output** como dispositivo de entrada. O app mistura sua voz física com os sons nesse dispositivo.
7. Adicione MP3 ou WAV pelo botão ou arrastando arquivos para a janela. Clique em **Tocar** ou configure um atalho em **Editar**.

O volume dos sons nos alto-falantes e o volume dos sons enviados junto à sua voz têm controles separados. Use o slider ou os botões **−/+** para ajustar os sons no microfone virtual em passos de 5%; sua voz mantém o nível definido no Windows. Ao iniciar um novo som, o anterior para. **Parar tudo** interrompe o som, mantendo sua voz no chat.

O atalho global para parar é **Ctrl+Alt+Backspace**. Cada som pode receber um atalho com `Ctrl+Alt`, `Ctrl+Shift` ou `Alt+Shift` e uma letra, número ou tecla F1–F12.

Ao minimizar ou fechar a janela, o getSoundBoard continua ativo na área de notificação do Windows para manter a voz e os atalhos funcionando. Clique duas vezes no ícone para reabrir. O menu do ícone permite **Abrir**, **Parar tudo** ou **Sair**.

Na aba **Atualizações**, clique em **Verificar atualizações** para ver as versões estáveis publicadas, as notas de cada versão e escolher o instalador para baixar. Os downloads abrem pelo GitHub Releases; a instalação é iniciada por você.

Se um dispositivo for desconectado, escolha os dispositivos novamente e clique em **Aplicar**. Se um atalho já estiver em uso por outro programa, o app informa o conflito. Alguns jogos com captura exclusiva de teclado podem impedir atalhos globais; os botões da janela continuam disponíveis.

## Compilar

Requer Windows x64, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) e as ferramentas de desenvolvimento WinUI/Windows App SDK do Visual Studio. O projeto usa C# com WinUI 3 e NAudio.

```powershell
dotnet restore getSoundBoard.csproj
dotnet build getSoundBoard.csproj -c Release -p:Platform=x64
dotnet publish getSoundBoard.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
```

O workflow em `.github/workflows/build.yml` compila a versão Windows e gera o instalador com Inno Setup. Uma tag `v*` anexa o instalador ao GitHub Release. O instalador inicial não é assinado; o Windows pode mostrar um aviso de editor desconhecido.

## Dados e licença

Os arquivos importados e as configurações ficam em `%LOCALAPPDATA%\getSoundBoard`. O app não usa conta nem serviço online. Código sob licença [MIT](LICENSE). VB-CABLE é um produto da [VB-Audio](https://vb-cable.com/), distribuído sob modelo donationware; o pacote do driver não é redistribuído pelo getSoundBoard. Se o produto for útil, conheça as opções de apoio no site oficial. VB-CABLE e NAudio mantêm suas próprias licenças.
