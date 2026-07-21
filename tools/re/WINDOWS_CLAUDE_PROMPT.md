# Prompt para o Claude do Windows

Copie **tudo** do bloco abaixo (da linha `---8<---` até o fim) e cole no Claude do Windows.

---8<---

## Contexto

Tenho um **ventilador holográfico de LED** (POV display) que comprei — modelo **42-F2**
(42 cm / 16,5", 224 LEDs, 2 pás). Ele toca um formato proprietário `.BIN`, gerado por um
software Windows fechado que veio no cartão SD do próprio aparelho.

Estou fazendo **engenharia reversa de interoperabilidade** do formato `.BIN` (é o meu
aparelho, é o meu direito) para escrever meu próprio codificador open-source e não depender
do software do fabricante. Já descobri, analisando os `.BIN` de demonstração de fábrica:

- frame = **129.024 bytes**, sem header global
- fatia (slice) = **252 bytes**, **512 fatias por frame**
- 252 B = **224 LEDs × 9 bits** → RGB de **3 bits por canal**
- os dados são **bit-planar** (não empacotados por pixel)
- provável layout: 2 pás × 126 B, cada pá = 9 planos × 14 B × 112 LEDs

**O que falta:** o mapeamento exato de bit→canal (qual plano é R, G, B) e dos eixos
angular/radial. Isso se resolve codificando **padrões de teste conhecidos** e olhando os
bytes que saem. É isso que preciso que você me ajude a fazer.

## Sua tarefa

1. Gerar ~11 vídeos de teste com ffmpeg.
2. Eu (humano) vou passar cada um pelo software do fabricante para gerar os `.BIN`.
3. Você roda um script de análise e me devolve um **relatório compacto** que eu vou colar
   de volta na outra conversa.

⚠️ **Aviso de segurança:** o software do fabricante veio de um cartão SD chinês e é
**não-confiável**. Eu assumo o risco de executá-lo (é o software do meu próprio aparelho).
**Você não precisa executá-lo** — quem clica na GUI sou eu. Você só gera vídeos e lê bytes.

---

### PASSO 1 — ffmpeg

Verifique se há ffmpeg: `ffmpeg -version`. Se não houver, o próprio software do fabricante
traz um `ffmpeg.exe` (procure na pasta dele, ~55 MB) — pode usar esse. Ou baixe de
https://www.gyan.dev/ffmpeg/builds/ (release essentials).

### PASSO 2 — gerar os padrões de teste

Crie a pasta `C:\holofan_re\patterns` e gere os vídeos abaixo (480×480, 2 s).
**Os 8 primeiros são PRIORIDADE** — são eles que resolvem quase tudo. Use comandos simples
(sem filtro), que não têm problema de escape:

```
ffmpeg -y -f lavfi -i color=black:s=480x480:r=25    -t 2 -pix_fmt yuv420p black.mp4
ffmpeg -y -f lavfi -i color=white:s=480x480:r=25    -t 2 -pix_fmt yuv420p white.mp4
ffmpeg -y -f lavfi -i color=0xFF0000:s=480x480:r=25 -t 2 -pix_fmt yuv420p solid_red.mp4
ffmpeg -y -f lavfi -i color=0x00FF00:s=480x480:r=25 -t 2 -pix_fmt yuv420p solid_green.mp4
ffmpeg -y -f lavfi -i color=0x0000FF:s=480x480:r=25 -t 2 -pix_fmt yuv420p solid_blue.mp4
ffmpeg -y -f lavfi -i color=0x808080:s=480x480:r=25 -t 2 -pix_fmt yuv420p half_gray.mp4
ffmpeg -y -f lavfi -i color=black:s=480x480:r=25 -vf "drawbox=x=236:y=236:w=8:h=8:color=white:t=fill" -t 2 -pix_fmt yuv420p center_dot.mp4
ffmpeg -y -f lavfi -i color=black:s=480x480:r=25 -vf "drawbox=x=240:y=239:w=240:h=3:color=white:t=fill" -t 2 -pix_fmt yuv420p spoke_0deg.mp4
```

**Bônus** (usam expressões com vírgula — para evitar problemas de escape no shell, escreva o
filtro num arquivo e use `-filter_script:v`):

- `ring_mid.mp4` — filtro: `format=gray,geq=lum='if(lt(abs(hypot(X-240,Y-240)-120),2),255,0)'`
- `radial_gradient.mp4` — filtro: `format=gray,geq=lum='255*hypot(X-240,Y-240)/240'`
- `two_frames.mp4` — fonte `color=black:s=480x480:r=2`, filtro:
  `format=rgba,geq=r='if(lt(T,1),255,0)':g='0':b='if(lt(T,1),0,255)'`

Exemplo: salve o filtro em `ring.txt` e rode
`ffmpeg -y -f lavfi -i color=black:s=480x480:r=25 -filter_script:v ring.txt -t 2 -pix_fmt yuv420p ring_mid.mp4`

Confirme comigo que todos foram gerados antes de seguir.

### PASSO 3 — me dê o checklist da GUI (eu clico)

Me liste os passos e espere eu confirmar. Eu vou, para **cada** vídeo:

1. Abrir o software do fabricante (`@Windows app(V13.0).exe`)
2. Selecionar o modelo de dispositivo **42-F2 (16,5 polegadas)** — isto é crítico
3. Clicar em **"Decodificar vídeo" / "Convert Video"** e escolher o `.mp4`
4. Dar o nome de saída **igual ao do vídeo** (ex.: `solid_red`)
5. Escolher o modo de decodificação **"Colorido"** (colored)
6. No enquadramento do **anel vermelho**: deixar **centralizado e com zoom mínimo**
   (o círculo cobrindo o máximo do quadro) — **igual em todos**, consistência importa mais
   que precisão
7. Clicar em **"Iniciar decodificação"** e salvar o `.BIN` em `C:\holofan_re\bins\`

**Além disso**, codifique o `solid_red.mp4` mais 2 vezes, nos outros modos, salvando como
`solid_red_comum.bin` e `solid_red_destaque.bin` — quero saber se o modo muda o formato.

Anote e me diga: o modelo exato que apareceu na lista e os nomes dos 3 modos.

### PASSO 4 — rodar o analisador

Quando eu disser que os `.BIN` estão prontos em `C:\holofan_re\bins\`, rode este script
(Python 3; se não houver, me avise que instalamos). Ele **só lê bytes**, não executa nada:

```python
import os, glob
FRAME, SLICE = 129024, 252
print("### DIGEST HOLOFAN ###")
for p in sorted(glob.glob(r"C:\holofan_re\bins\*.bin") + glob.glob(r"C:\holofan_re\bins\*.BIN")):
    d = open(p, "rb").read()
    n = os.path.basename(p); s = len(d)
    q, r = divmod(s, FRAME)
    print(f"\n--- {n}")
    print(f"size={s} frames={q} rem={r}")
    print(f"head32={d[:32].hex()}")
    # fatia 0 do frame 0 e de um frame do meio
    for label, off in (("f0_s0", 0), ("fmid_s0", (q//2)*FRAME if q > 1 else 0)):
        sl = d[off:off+SLICE]
        print(f"{label}={sl.hex()}")
    # quais fatias do frame do meio não são todas-zero (revela eixo angular)
    if q:
        base = (q//2)*FRAME
        nz = [i for i in range(512) if any(d[base+i*SLICE: base+(i+1)*SLICE])]
        print(f"nonzero_slices={len(nz)}/512 first={nz[:12]}")
print("\n### FIM ###")
```

### PASSO 5 — me devolver o resultado

Monte um **prompt final** para eu colar na outra conversa, contendo:

- a saída **completa e literal** do script (todo o hex, sem cortar)
- o modelo de dispositivo selecionado e os nomes dos 3 modos de decodificação
- qualquer coisa estranha que você/eu notamos (erros, o software recusar algum vídeo,
  o `.BIN` sair com tamanho inesperado, etc.)

Formate como um bloco de código único, começando com
`Aqui está o digest do encoder do fabricante:`.

---8<---
