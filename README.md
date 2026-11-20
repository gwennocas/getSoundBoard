# getSoundBoard

Soundboard open source para Windows 10/11, migrado para Rust. Toque WAV, MP3, OGG/Vorbis, FLAC, AIFF, AAC, M4A e MP4 por botões ou atalhos globais. O app pode tocar só nos alto-falantes ou enviar sua voz e os sons a um microfone virtual.

## Recursos

- Sons simultâneos, repetição, pausa, reprodução aleatória e controles de volume separados.
- Categorias, busca, importação de pastas, gravação de microfone e editor de trecho com forma de onda.
- Atalhos globais individuais, por posição e para controlar a reprodução.
- Ganho, RNNoise ajustável e EQ de clareza opcionais para a voz, além de normalização de pico por som.
- Área de notificação e inicialização com o Windows; biblioteca C# existente importada na primeira abertura.

## Como usar

1. Abra o getSoundBoard e clique em **Conectar áudio** para usar os dispositivos selecionados. Para tocar apenas nos alto-falantes, escolha o modo **Só fone**; essa rota não precisa de cabo virtual.
2. Para enviar a voz e os sons ao Discord ou jogo, instale o [VB-CABLE oficial](https://vb-audio.com/Cable/index.htm). O driver não acompanha o instalador do getSoundBoard. Escolha seu microfone físico, alto-falantes/fone e **CABLE Input** no app; no Discord ou jogo, escolha **CABLE Output** como entrada.
3. Adicione arquivos pelo botão ou arrastando-os para a janela. Clique em **Tocar** ou configure um atalho em **Editar**.

São aceitos WAV, MP3, OGG/Vorbis, FLAC, AIFF, AAC, M4A e arquivos MP4 com faixa de áudio compatível.

O volume dos sons nos alto-falantes e o volume dos sons enviados junto à sua voz têm controles separados. Use o slider ou os botões **−/+** para ajustar os sons no microfone virtual em passos de 5%. O controle **Ganho da sua voz** ajusta apenas o microfone antes do RNNoise; 100% mantém o nível original, e a alteração é aplicada ao vivo e salva. **Supressão de ruído na voz** aplica RNNoise somente ao microfone antes da mistura; pode ser ligada ou desligada sem reiniciar. **Intensidade da supressão** mistura o áudio filtrado com uma parcela alinhada do microfone original; comece em 50% e diminua se a voz sumir. Em 0%, o áudio do microfone passa sem filtro. O ajuste é aplicado ao vivo e salvo. **Clareza da voz** é um EQ opcional no microfone após a supressão: corta graves abaixo de 85 Hz e realça levemente a presença em 3 kHz, sem gate ou compressor. Ele não recupera partes da voz já removidas pelo RNNoise. **Sobrepor sons** permite que vários sons toquem juntos. O mixer limita picos para evitar clipping. **Parar tudo** interrompe todos os sons, mantendo sua voz no chat.

O atalho global para parar é **Ctrl+Alt+Backspace**. Cada som pode receber um atalho com Ctrl, Alt, Shift ou combinações deles e uma letra, número da fileira superior ou tecla F1–F12. O editor também tem um bloco separado para Numpad 0–9, decimal, +, −, *, / e Enter, com ou sem modificadores. As teclas físicas do numpad continuam sendo reconhecidas com NumLock desligado; as teclas de navegação fora do numpad não se confundem com elas. O app detecta conflitos externos dos atalhos registrados no Windows. Para Enter do numpad e o modo NumLock desligado, o hook de teclado não permite detectar todos os conflitos externos.

Ao minimizar ou fechar a janela, o getSoundBoard continua ativo na área de notificação do Windows para manter a voz e os atalhos funcionando. Clique duas vezes no ícone para reabrir. O menu do ícone permite **Abrir**, **Parar tudo** ou **Sair**.

**Iniciar junto com o Windows** registra o app para o usuário atual, sem pedir administrador. Ao entrar no Windows, ele abre na área de notificação e conecta o áudio com os dispositivos salvos. Desligar a opção remove o registro. Se você mover o app para outra pasta, abrir o executável novo atualiza o caminho salvo para a próxima inicialização.

Se um dispositivo for desconectado, escolha os dispositivos novamente e clique em **Aplicar**. Se um atalho já estiver em uso por outro programa, o app informa o conflito. Alguns jogos com captura exclusiva de teclado podem impedir atalhos globais; os botões da janela continuam disponíveis.

## Compilar

Requer Windows x64 e o toolchain estável do Rust. Na pasta `rust`:

```powershell
cargo build --release --locked
```

O workflow em `.github/workflows/rust.yml` compila o app e o instalador Inno Setup. Uma tag `v*` publica um GitHub Release com notas geradas e anexa o instalador. O instalador não é assinado; o Windows pode mostrar um aviso de editor desconhecido. O workflow C# antigo permanece apenas para compilar a versão anterior.

## Dados e licença

Os arquivos importados e as configurações ficam em `%LOCALAPPDATA%\getSoundBoard`. O app não usa conta nem serviço online. Código sob licença [MIT](LICENSE). O app Rust usa [nnnoiseless](https://github.com/jneem/nnnoiseless), licença BSD-3-Clause. A versão C# anterior usa NAudio e [RNNoise.NET](https://github.com/Yellow-Dog-Man/RNNoise.Net), além da biblioteca nativa [RNNoise](https://github.com/xiph/rnnoise). Os avisos estão em [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). VB-CABLE é um produto da [VB-Audio](https://vb-audio.com/), distribuído sob modelo donationware; o pacote do driver não é redistribuído pelo getSoundBoard.

## Biblioteca, janela e áudio

Na pasta `rust`, rode `cargo run --locked -- gui` para abrir a janela durante o desenvolvimento. Escolha fone, microfone e saída virtual no bloco **Áudio** e clique em **Conectar áudio**. A lista pode ser atualizada pelo botão **Atualizar dispositivos** após conectar ou remover um equipamento; reconecte o áudio se a rota ativa tiver mudado.
A biblioteca Rust permite buscar e categorizar sons, importar vários arquivos ou uma pasta inteira, e também
aceita arrastar arquivos para a janela. Em **Editar**, altere nome, categoria,
volume individual, normalize o pico sem alterar o arquivo, defina um trecho de
reprodução ou configure atalho global. Os atalhos globais incluem os sons individuais, **Ctrl+Alt+Backspace**
para parar tudo, **Ctrl+Alt+P** para pausar/retomar, **Ctrl+Alt+←/→** para o som
anterior/próximo e **Ctrl+Alt+R** para tocar aleatoriamente. A opção **Ctrl+Alt+1–0**
ativa atalhos por posição para os dez primeiros sons. O modo de reprodução pode
usar fone e chat, só fone ou só chat; repetir e sobrepor sons são opções
persistidas. No Windows, os atalhos globais são registrados pelo sistema.

O motor Rust decodifica WAV, MP3, OGG/Vorbis, FLAC, AIFF, AAC, M4A e faixas de
áudio compatíveis em MP4. Também grava o microfone em WAV (até 5 minutos por
gravação) e permite definir um trecho ou normalizar o pico por som sem alterar
o arquivo original. O ganho de normalização é limitado a 60 dB para não
amplificar silêncio extremo. Quando a voz é enviada ao chat, o app oferece
ganho do microfone, supressão RNNoise ajustável e EQ opcional de clareza; esses
filtros afetam apenas a voz. O editor mostra a forma de onda para ajustar o
trecho arrastando as bordas. Normalização de loudness segue em desenvolvimento.
No Windows, fechar a janela mantém o app na área de notificação; o menu permite
reabrir, parar todos os sons ou sair. **Iniciar junto com o Windows** registra
o executável para o usuário atual; ao entrar no sistema ele abre escondido na
bandeja e reconecta os dispositivos salvos. Se o executável for movido, abrir a
nova cópia atualiza o caminho de inicialização.

Os arquivos importados ficam em `RustAudio` e a lista em `rust-library.json` na pasta local de dados do getSoundBoard. Na primeira abertura no Windows, a janela lê os títulos, atalhos e referências a arquivos de `library.json`. Remover um item da lista preserva o arquivo de áudio.
