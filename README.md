# getSoundBoard

Soundboard open source para Windows 10/11. Toque MP3, WAV, OGG/Vorbis, Ogg/Opus, FLAC, AIFF, M4A, AAC e WMA por botões ou atalhos globais. O app envia sua voz e os sons para um microfone virtual e permite ouvir os sons em volume separado nos seus alto-falantes.

## Novidades da v0.1.5

- Supressão RNNoise ajustável só no microfone, com ganho e EQ opcional de clareza da voz.
- Sons simultâneos com limitação de picos para evitar clipping no mix.
- Atalhos individuais para o teclado numérico, inclusive Enter e uso com NumLock desligado.
- Opção para iniciar na área de notificação junto com o Windows.

## Como usar

1. No app, clique em **Baixar ZIP oficial do VB-Audio**. O download também está disponível na [página oficial do VB-CABLE](https://vb-audio.com/Cable/index.htm). O driver não acompanha o instalador do getSoundBoard.
2. Abra o ZIP baixado e extraia todos os arquivos para uma pasta.
3. Na pasta extraída, clique com o botão direito no instalador de 64 bits (x64), escolha **Executar como administrador** e permita a instalação do driver. O instalador oficial pode pedir confirmação do Windows.
4. Reinicie o Windows para o novo dispositivo de áudio aparecer.
5. Abra o getSoundBoard, clique em **Atualizar dispositivos** e escolha seu microfone físico e alto-falantes/fone. Se encontrado, o app seleciona **CABLE Input** automaticamente. Clique em **Aplicar**.
6. No Discord ou jogo, escolha **CABLE Output** como dispositivo de entrada. O app mistura sua voz física com os sons nesse dispositivo.
7. Adicione arquivos pelo botão ou arrastando-os para a janela. São aceitos MP3, WAV, OGG/Vorbis, Ogg/Opus, FLAC, AIFF, M4A, AAC e WMA. Clique em **Tocar** ou configure um atalho em **Editar**.

OGG/Vorbis e Ogg/Opus são decodificados pelo app. FLAC, AAC, M4A e WMA usam os codecs de áudio do Windows; a disponibilidade pode variar conforme a edição e os codecs instalados.

O volume dos sons nos alto-falantes e o volume dos sons enviados junto à sua voz têm controles separados. Use o slider ou os botões **−/+** para ajustar os sons no microfone virtual em passos de 5%. O controle **Ganho da sua voz** ajusta apenas o microfone antes do RNNoise; 100% mantém o nível original, e a alteração é aplicada ao vivo e salva. **Supressão de ruído na voz** aplica RNNoise somente ao microfone antes da mistura; pode ser ligada ou desligada sem reiniciar. **Intensidade da supressão** mistura o áudio filtrado com uma parcela alinhada do microfone original; comece em 50% e diminua se a voz sumir. Em 0%, o áudio do microfone passa sem filtro. O ajuste é aplicado ao vivo e salvo. **Clareza da voz** é um EQ opcional no microfone após a supressão: corta graves abaixo de 85 Hz e realça levemente a presença em 3 kHz, sem gate ou compressor. Ele não recupera partes da voz já removidas pelo RNNoise. **Sobrepor sons** permite que vários sons toquem juntos. O mixer limita picos para evitar clipping. **Parar tudo** interrompe todos os sons, mantendo sua voz no chat.

O atalho global para parar é **Ctrl+Alt+Backspace**. Cada som pode receber um atalho com Ctrl, Alt, Shift ou combinações deles e uma letra, número da fileira superior ou tecla F1–F12. O editor também tem um bloco separado para Numpad 0–9, decimal, +, −, *, / e Enter, com ou sem modificadores. As teclas físicas do numpad continuam sendo reconhecidas com NumLock desligado; as teclas de navegação fora do numpad não se confundem com elas. O app detecta conflitos externos dos atalhos registrados no Windows. Para Enter do numpad e o modo NumLock desligado, o hook de teclado não permite detectar todos os conflitos externos.

Ao minimizar ou fechar a janela, o getSoundBoard continua ativo na área de notificação do Windows para manter a voz e os atalhos funcionando. Clique duas vezes no ícone para reabrir. O menu do ícone permite **Abrir**, **Parar tudo** ou **Sair**.

**Iniciar junto com o Windows** registra o app para o usuário atual, sem pedir administrador. Ao entrar no Windows, ele abre na área de notificação e conecta o áudio com os dispositivos salvos. Desligar a opção remove o registro. Se você mover o app para outra pasta, abrir o executável novo atualiza o caminho salvo para a próxima inicialização.

Na aba **Atualizações**, clique em **Verificar atualizações** para ver as versões estáveis publicadas, as notas de cada versão e escolher o instalador para baixar. Os downloads abrem pelo GitHub Releases; a instalação é iniciada por você.

Se um dispositivo for desconectado, escolha os dispositivos novamente e clique em **Aplicar**. Se um atalho já estiver em uso por outro programa, o app informa o conflito. Alguns jogos com captura exclusiva de teclado podem impedir atalhos globais; os botões da janela continuam disponíveis.

## Compilar

Requer Windows x64, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) e as ferramentas de desenvolvimento WinUI/Windows App SDK do Visual Studio. O projeto usa C# com WinUI 3 e NAudio.

```powershell
dotnet restore getSoundBoard.csproj
dotnet build getSoundBoard.csproj -c Release -p:Platform=x64
dotnet publish getSoundBoard.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
```

O workflow em `.github/workflows/build.yml` compila a versão Windows e gera o instalador com Inno Setup. Uma tag `v*` cria um GitHub Release com notas geradas e anexa o instalador. O instalador não é assinado; o Windows pode mostrar um aviso de editor desconhecido.

## Dados e licença

Os arquivos importados e as configurações ficam em `%LOCALAPPDATA%\getSoundBoard`. O app não usa conta nem serviço online. Código sob licença [MIT](LICENSE). O wrapper [RNNoise.NET](https://github.com/Yellow-Dog-Man/RNNoise.Net) é MIT e a biblioteca nativa [RNNoise](https://github.com/xiph/rnnoise) é BSD-3-Clause; os textos das licenças estão em [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). VB-CABLE é um produto da [VB-Audio](https://vb-cable.com/), distribuído sob modelo donationware; o pacote do driver não é redistribuído pelo getSoundBoard. Se o produto for útil, conheça as opções de apoio no site oficial. VB-CABLE e NAudio mantêm suas próprias licenças.
