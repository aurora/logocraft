module Logo.Client.Examples

let flower =
    """; Blume aus Quadraten
make "colors [red orange magenta blue green purple]

to square :size
  repeat 4 [fd :size rt 90]
end

to flower :n
  repeat :n [
    setpc item (repcount % 6 + 1) :colors
    square 80
    rt 360 / :n
  ]
end

setpensize 2
flower 24
"""

let star =
    """; Stern
setpensize 3
setpc "red
repeat 5 [fd 150 rt 144]
"""

let spiral =
    """; Quadratspirale
make "colors [red orange green blue purple]

to spiral :size
  if :size > 250 [stop]
  setpc item (int (:size / 20) % 5 + 1) :colors
  fd :size
  rt 91
  spiral :size + 3
end

spiral 5
"""

let tree =
    """; Rekursiver Baum
to tree :size
  if :size < 6 [stop]
  setpensize :size / 12 + 1
  ifelse :size < 20 [setpc "green] [setpc "brown]
  fd :size
  lt 25
  tree :size * 0.7
  rt 50
  tree :size * 0.7
  lt 25
  pu bk :size pd
end

pu setxy 0 -200 pd
tree 90
"""

let koch =
    """; Koch-Schneeflocke
to koch :n :len
  if :n = 0 [fd :len stop]
  koch :n - 1 :len / 3
  lt 60
  koch :n - 1 :len / 3
  rt 120
  koch :n - 1 :len / 3
  lt 60
  koch :n - 1 :len / 3
end

pu setxy -150 80 pd
setpc "blue
repeat 3 [koch 3 300 rt 120]
"""

let circles =
    """; Kreise mit arc
make "colors [red orange green cyan blue purple]
setpensize 2
for [i 1 12] [
  setpc item (:i % 6 + 1) :colors
  arc 360 :i * 12
]
"""

let all : (string * string) list =
    [ "Blume", flower
      "Stern", star
      "Spirale", spiral
      "Baum", tree
      "Koch-Schneeflocke", koch
      "Kreise", circles ]
