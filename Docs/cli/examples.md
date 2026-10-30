<!--
  An example for every verb, placed under its entry in Docs/CLI.md by `jazz docs`. Each heading is
  a verb's path with dots (clip.add); the lines under it are the example: lines starting with jazz
  are commands, lines starting with # are comments. Ids are placeholders in angle brackets:
  <v1-id> is the first video track's id, <clip-id> a clip's, <capture-id> a media item's, and so
  on, as `jazz describe trailer.jazz --detail full` shows them. A verb whose note in notes/
  already has an example is not repeated here. A test parses every line against the real
  commands, and checks every verb has one.
-->

## frame
jazz frame trailer.jazz --at 00:00:12.500 --out check.png

## frames
jazz frames trailer.jazz --every 5s --out frames

## contact-sheet
jazz contact-sheet trailer.jazz --out sheet.png --cols 4 --rows 3

## proof
jazz proof trailer.jazz --out proof.mp4

## apply
jazz apply trailer.jazz edits.json

## docs
jazz docs --out Docs\CLI.md

## serve
jazz serve trailer.jazz --save-on-exit

## mcp
# Add it to Claude Code, connected to the running editor:
# claude mcp add jazz -- jazz mcp --attach
jazz mcp --list-tools

## describe
jazz describe trailer.jazz --detail full

## redo
jazz --attach redo

## undo
# Undo belongs to an open session, so it goes to a running editor.
jazz --attach undo --steps 2

## app.hide
jazz --attach app hide

## app.quit
jazz --attach app quit --wait-for-exports

## app.show
jazz --attach app show

## app.unregister
jazz --attach app unregister

## audio.beat-analysis
jazz audio beat-analysis trailer.jazz <music-clip-id>

## audio.beats
jazz audio beats trailer.jazz <music-clip-id>

## audio.detach
jazz audio detach trailer.jazz <clip-id>

## audio.duck
jazz audio duck trailer.jazz --music <music-track-id> --voice <a1-id> --depth -12

## audio.enhance-speech
jazz audio enhance-speech trailer.jazz <voice-clip-id> --amount 0.8

## audio.learn-noise
jazz audio learn-noise trailer.jazz <voice-clip-id> --from 00:00:00.500 --to 00:00:01.500

## audio.measure
jazz audio measure trailer.jazz --mix

## audio.meter
jazz audio meter trailer.jazz --from 00:00:10.000 --to 00:00:20.000

## audio.mute-stream
jazz audio mute-stream trailer.jazz <clip-id> --stream 2 --muted

## audio.normalize
jazz audio normalize trailer.jazz --mix --mode lufs --target -14

## audio.reduce-noise
jazz audio reduce-noise trailer.jazz <voice-clip-id> --reduction 12

## audio.replace
jazz audio replace trailer.jazz <voice-clip-id> <mic-id>

## audio.set-channel-map
jazz audio set-channel-map trailer.jazz <voice-clip-id> left

## audio.set-fade-in
jazz audio set-fade-in trailer.jazz <music-clip-id> --dur 2s --curve ease-in

## audio.set-fade-out
jazz audio set-fade-out trailer.jazz <music-clip-id> --dur 3s

## audio.set-gain
jazz audio set-gain trailer.jazz <music-clip-id> --db -6

## audio.set-limiter
jazz audio set-limiter trailer.jazz --on --ceiling -1

## audio.set-master-volume
jazz audio set-master-volume trailer.jazz --db -3

## audio.set-pan
jazz audio set-pan trailer.jazz <voice-clip-id> --pan -0.3

## audio.sync
jazz audio sync trailer.jazz --clip <voice-clip-id> --to <clip-id>

## audio.sync-offset
jazz audio sync-offset trailer.jazz --clip <voice-clip-id> --to <clip-id>

## cache.clear
jazz cache clear trailer.jazz --thumbs --waveforms

## cache.configure
jazz cache configure trailer.jazz --cap-gb 40

## cache.stats
jazz cache stats trailer.jazz

## camera.add
jazz camera add trailer.jazz --at 0 --dur 6s --orbit 30 --dolly 400

## chapter.add
jazz chapter add trailer.jazz "Boss fight" --at 00:01:10.000

## chapter.from-markers
jazz chapter from-markers trailer.jazz

## chapter.import
jazz chapter import trailer.jazz <capture-id>

## chapter.list
jazz chapter list trailer.jazz

## clip.add
jazz clip add trailer.jazz <v1-id> --media <capture-id> --at 00:00:30.000 --in 00:01:10.000 --dur 6s
# A generator rather than a file:
jazz clip add trailer.jazz <v2-id> --generator gen.solid --at 00:00:10.000 --dur 3s

## clip.add-adjustment
jazz clip add-adjustment trailer.jazz --at 00:00:10.000 --dur 5s

## clip.close-gap
jazz clip close-gap trailer.jazz <v1-id> --at 00:00:13.000

## clip.copy
jazz clip copy trailer.jazz <clip-id> --at 00:00:30.000 --track <v2-id>

## clip.duplicate
jazz clip duplicate trailer.jazz <title-clip-id>

## clip.extract
jazz clip extract trailer.jazz --from 00:00:05.000 --to 00:00:07.000

## clip.find-fillers
jazz clip find-fillers trailer.jazz <voice-clip-id> --pauses 1s

## clip.freeze-frame
jazz clip freeze-frame trailer.jazz <clip-id> --at 00:00:03.000 --dur 2s

## clip.get
jazz clip get trailer.jazz <clip-id>

## clip.group
jazz clip group trailer.jazz <clip-id> <title-clip-id>

## clip.insert
jazz clip insert trailer.jazz <v1-id> --media <capture-id> --at 00:00:04.000 --dur 3s

## clip.insert-from-source
jazz clip insert-from-source trailer.jazz --media <capture-id> --source-in 00:01:10.000 --source-out 00:01:14.000 --at 00:00:20.000

## clip.lift
jazz clip lift trailer.jazz --from 00:00:05.000 --to 00:00:07.000

## clip.link
jazz clip link trailer.jazz <clip-id> <voice-clip-id>

## clip.list
jazz clip list trailer.jazz --track <v1-id>

## clip.match-frame
jazz clip match-frame trailer.jazz <clip-id> --at 00:00:05.000

## clip.measure-3d
jazz clip measure-3d trailer.jazz <3d-clip-id> --at 00:00:01.000

## clip.move
jazz clip move trailer.jazz <clip-id> --to 00:00:12.000 --track <v2-id>

## clip.nest
jazz clip nest trailer.jazz <clip-id>,<next-clip-id> "Opening"

## clip.nudge
jazz clip nudge trailer.jazz <title-clip-id> --frames 2

## clip.overwrite
jazz clip overwrite trailer.jazz <v1-id> --media <capture-id> --at 00:00:06.000 --dur 2s

## clip.overwrite-from-source
jazz clip overwrite-from-source trailer.jazz --media <capture-id> --source-in 00:02:00.000 --source-out 00:02:03.000 --at 00:00:06.000

## clip.paste
# --data is the JSON jazz clipboard copy prints. A command line holds about 32,000 characters,
# so pass a few clips at a time. In PowerShell:
# $clips = jazz clipboard copy trailer.jazz <next-clip-id> | Out-String
jazz clip paste trailer.jazz --data $clips --at 00:00:40.000 --track <v1-id>

## clip.plan-from-source
jazz clip plan-from-source trailer.jazz --media <capture-id> --source-in 00:01:10.000 --source-out 00:01:14.000 --at 00:00:20.000

## clip.ramp-speed
jazz clip ramp-speed trailer.jazz <clip-id> --from 1 --to 0.25 --at 00:00:02.000 --dur 1s

## clip.rate-stretch
jazz clip rate-stretch trailer.jazz <clip-id> --dur 4s

## clip.remap-add-point
jazz clip remap-add-point trailer.jazz <clip-id> --at 00:00:02.000 --speed 0.5

## clip.remap-move-point
jazz clip remap-move-point trailer.jazz <clip-id> --point 1 --at 00:00:02.500

## clip.remap-remove-point
jazz clip remap-remove-point trailer.jazz <clip-id> --point 1

## clip.remap-set-ease
jazz clip remap-set-ease trailer.jazz <clip-id> --point 1 --ease ease-in-out

## clip.remove
jazz clip remove trailer.jazz <clip-id> --ripple

## clip.remove-background
jazz clip remove-background trailer.jazz <clip-id>

## clip.remove-fillers
jazz clip remove-fillers trailer.jazz <voice-clip-id> --pauses 1s --keep 200ms

## clip.remove-words
jazz clip remove-words trailer.jazz <voice-clip-id> --from 12 --to 15

## clip.ripple-delete
# Their linked sound goes too, unless --alone.
jazz clip ripple-delete trailer.jazz <clip-id> <next-clip-id>

## clip.ripple-trim
jazz clip ripple-trim trailer.jazz <clip-id> --edge end --to 00:00:03.500

## clip.roll
jazz clip roll trailer.jazz <clip-id> <next-clip-id> --by 12f

## clip.set-3d
jazz clip set-3d trailer.jazz <clip-id> --lights --casts-shadows

## clip.set-blend
jazz clip set-blend trailer.jazz <title-clip-id> screen

## clip.set-crop
# Each edge is a percentage of the picture.
jazz clip set-crop trailer.jazz <clip-id> --top 10 --bottom 10

## clip.set-enabled
jazz clip set-enabled trailer.jazz <clip-id> false

## clip.set-fast-mute
jazz clip set-fast-mute trailer.jazz <clip-id> --above 2

## clip.set-input-transform
jazz clip set-input-transform trailer.jazz <clip-id> slog3

## clip.set-keep-pitch
jazz clip set-keep-pitch trailer.jazz <clip-id> true

## clip.set-matte
jazz clip set-matte trailer.jazz <clip-id> --source <v2-id> --mode luma

## clip.set-motion-blur
jazz clip set-motion-blur trailer.jazz <title-clip-id> --angle 180 --samples 16

## clip.set-opacity
jazz clip set-opacity trailer.jazz <clip-id> --opacity 0.5

## clip.set-remap
jazz clip set-remap trailer.jazz <clip-id>

## clip.set-retime
jazz clip set-retime trailer.jazz <clip-id> optical-flow

## clip.set-reverse
jazz clip set-reverse trailer.jazz <clip-id> true

## clip.set-speed
jazz clip set-speed trailer.jazz <clip-id> --speed 2 --keep-pitch

## clip.set-speed-blur
jazz clip set-speed-blur trailer.jazz <clip-id> true

## clip.set-tone-map
jazz clip set-tone-map trailer.jazz <clip-id> --operator hable --peak 1000

## clip.set-transform
jazz clip set-transform trailer.jazz <clip-id> --x 200 --y -100 --scale 0.5

## clip.slide
jazz clip slide trailer.jazz <clip-id> --by 1s

## clip.slip
jazz clip slip trailer.jazz <clip-id> --by -500ms

## clip.speed-preset
jazz clip speed-preset trailer.jazz <clip-id> impact --at 00:00:02.000

## clip.split
jazz clip split trailer.jazz <clip-id> --at 00:00:02.500

## clip.split-at-cuts
jazz clip split-at-cuts trailer.jazz <clip-id> --linked

## clip.stabilize
jazz clip stabilize trailer.jazz <clip-id> --smoothing 30

## clip.storyline-move
jazz clip storyline-move trailer.jazz <clip-id> --to 00:00:10.000

## clip.trim
jazz clip trim trailer.jazz <clip-id> --in 00:00:01.000 --out 00:00:04.000

## clip.ungroup
jazz clip ungroup trailer.jazz <clip-id>

## clip.unlink
jazz clip unlink trailer.jazz <clip-id>

## clip.unnest
jazz clip unnest trailer.jazz <compound-clip-id>

## clipboard.copy
jazz clipboard copy trailer.jazz <clip-id> <next-clip-id>

## color.match
jazz color match trailer.jazz <clip-id> --to <next-clip-id>

## color.node-add
jazz color node-add trailer.jazz <clip-id> --type curves

## color.node-connect
# The curves read the picture coming into the graph rather than the wheels.
jazz color node-connect trailer.jazz <colour-node-id>

## color.node-remove
jazz color node-remove trailer.jazz <node-id>

## color.node-set
jazz color node-set trailer.jazz <node-id> --param saturation --value 1.2

## color.sample
# The point is in pixels from the centre of the frame.
jazz color sample trailer.jazz --at 00:00:03.000 --x -400 --y 120

## color.to-graph
jazz color to-graph trailer.jazz <colour-effect-id>

## comp.create
jazz comp create trailer.jazz <next-clip-id>

## comp.describe
jazz comp describe trailer.jazz <clip-id>

## comp.group
jazz comp group trailer.jazz <comp-node-id> <merge-node-id>

## comp.node-add
jazz comp node-add trailer.jazz <clip-id> video.blur.gaussian --x 300 --y 120

## comp.node-connect
jazz comp node-connect trailer.jazz <merge-node-id> <comp-node-id> --port foreground

## comp.node-disconnect
jazz comp node-disconnect trailer.jazz <merge-node-id> --port foreground

## comp.node-move
jazz comp node-move trailer.jazz <comp-node-id> 400 200

## comp.node-remove
jazz comp node-remove trailer.jazz <comp-node-id>

## comp.ungroup
jazz comp ungroup trailer.jazz <group-node-id>

## comp.view
jazz --attach comp view <comp-node-id>

## diagnostics.list
jazz diagnostics list trailer.jazz --level warning

## edit.cut-to-beats
# Mark the music's beats first: jazz audio beats trailer.jazz <music-clip-id>
jazz edit cut-to-beats trailer.jazz --track <v1-id> --every 2 --clips <clip-id>,<next-clip-id>

## effect.add
jazz effect add trailer.jazz <clip-id> video.blur.gaussian

## effect.apply-preset
jazz effect apply-preset trailer.jazz <clip-id> "Teal and orange"

## effect.copy
jazz effect copy trailer.jazz <clip-id>

## effect.export-preset
jazz effect export-preset trailer.jazz "Teal and orange"

## effect.get
jazz effect get trailer.jazz <effect-id>

## effect.import-preset
# --data is the JSON jazz effect export-preset prints, from this project or another. In PowerShell:
# $preset = jazz effect export-preset other.jazz "Teal and orange" | Out-String
jazz effect import-preset trailer.jazz --data $preset

## effect.list
jazz effect list --kind transition

## effect.list-presets
jazz effect list-presets trailer.jazz

## effect.move
jazz effect move trailer.jazz <effect-id> --index 0

## effect.paste
# --data is the JSON jazz effect copy prints. In PowerShell:
# $effects = jazz effect copy trailer.jazz <clip-id> | Out-String
jazz effect paste trailer.jazz <next-clip-id> --data $effects

## effect.remove
jazz effect remove trailer.jazz <effect-id>

## effect.remove-preset
jazz effect remove-preset trailer.jazz "Teal and orange"

## effect.reset
jazz effect reset trailer.jazz <effect-id> --param radius

## effect.save-preset
jazz effect save-preset trailer.jazz <clip-id> --name "Teal and orange"

## effect.set-enabled
jazz effect set-enabled trailer.jazz <effect-id> false

## effect.set-param
jazz effect set-param trailer.jazz <effect-id> radius 12

## export.batch
jazz export batch trailer.jazz exports --markers --preset youtube-1080p

## export.cancel
jazz --attach export cancel <job-id>

## export.clear
jazz --attach export clear

## export.contact-sheet
jazz export contact-sheet trailer.jazz sheet.png --columns 6 --rows 4

## export.enqueue
jazz --attach export enqueue trailer.mp4 --preset youtube-4k --priority high

## export.list
jazz --attach export list

## export.log
jazz --attach export log <job-id>

## export.pause
jazz --attach export pause

## export.plan
jazz export plan trailer.jazz trailer.mp4 --preset discord-8mb

## export.resume
jazz --attach export resume <job-id>

## export.set-priority
jazz --attach export set-priority <job-id> high

## export.still
jazz export still trailer.jazz poster.png --at 00:00:12.500

## fonts.list
jazz fonts list --search Segoe

## history.list
jazz history list trailer.jazz --limit 20

## keyframe.add
jazz keyframe add trailer.jazz <clip-id> opacity --at 0 --value 0
jazz keyframe add trailer.jazz <clip-id> opacity --at 1s --value 1 --interp ease-out

## keyframe.move
jazz keyframe move trailer.jazz <clip-id> opacity --at 1s --to 2s

## keyframe.remove
jazz keyframe remove trailer.jazz <clip-id> opacity --at 1s

## keyframe.set-handles
jazz keyframe set-handles trailer.jazz <clip-id> opacity --at 1s --in "0.6, 1" --out "0.4, 0"

## keyframe.set-interp
jazz keyframe set-interp trailer.jazz <clip-id> opacity --at 1s --interp bezier

## keyframe.set-value
jazz keyframe set-value trailer.jazz <clip-id> opacity --at 1s --value 0.8

## layout.apply
jazz layout apply trailer.jazz facecam --clips <clip-id>,<facecam-clip-id> --corner bottom-left

## layout.list
jazz layout list trailer.jazz

## light.add
jazz light add trailer.jazz --kind spot --at 0 --dur 6s --intensity 2 --shadows

## marker.add
jazz marker add trailer.jazz "Big hit" --at 00:00:42.000 --color red

## marker.add-at-cuts
jazz marker add-at-cuts trailer.jazz <capture-id>

## marker.list
jazz marker list trailer.jazz --chapters

## marker.remove
jazz marker remove trailer.jazz <marker-id>

## marker.set
jazz marker set trailer.jazz <marker-id> --name "Drop" --at 00:00:44.000

## mask.add
jazz mask add trailer.jazz <clip-id> --shape ellipse --x 960 --y 540 --width 600 --height 400 --feather 40

## mask.remove
jazz mask remove trailer.jazz <mask-id>

## mask.set
jazz mask set trailer.jazz <mask-id> --feather 80 --invert

## media.add
jazz media add trailer.jazz .\captures\*.mp4 --tags gameplay

## media.add-subclip
jazz media add-subclip trailer.jazz <capture-id> --in 00:01:10.000 --out 00:01:22.500 --name "Boss fight"

## media.analyze-motion
jazz media analyze-motion trailer.jazz <capture-id>

## media.check
jazz media check trailer.jazz

## media.detect-cuts
jazz media detect-cuts trailer.jazz <capture-id>

## media.get
jazz media get trailer.jazz <capture-id>

## media.list
jazz media list trailer.jazz --tag gameplay

## media.missing
jazz media missing trailer.jazz --search D:\Recordings

## media.probe
jazz media probe .\captures\boss-fight.mp4

## media.relink
jazz media relink trailer.jazz --auto --search D:\Recordings

## media.remove
jazz media remove trailer.jazz <capture-id> --with-clips

## media.remove-unused
jazz media remove-unused trailer.jazz --keep-tagged

## media.replace
jazz media replace trailer.jazz <capture-id> .\captures\boss-fight-v2.mp4

## media.reprobe
jazz media reprobe trailer.jazz

## media.set
jazz media set trailer.jazz <capture-id> --name "Boss fight" --folder Gameplay

## media.subclips-from-cuts
jazz media subclips-from-cuts trailer.jazz <capture-id> --folder Shots

## media.unwatch
jazz media unwatch trailer.jazz D:\Recordings

## media.usage
jazz media usage trailer.jazz --unused

## media.watch
jazz media watch trailer.jazz D:\Recordings --tags obs

## media.watches
jazz media watches trailer.jazz

## model.download
jazz model download trailer.jazz whisper-large-v3-turbo

## model.list
jazz model list trailer.jazz

## model3d.add
jazz model3d add trailer.jazz robot.glb --at 0 --dur 5s --animation Walk

## model3d.info
jazz model3d info trailer.jazz robot.glb

## multicam.create
jazz multicam create trailer.jazz <capture-id> <cam2-id> --sync audio --name "Interview"

## multicam.flatten
jazz multicam flatten trailer.jazz <multicam-clip-id>

## multicam.set-sound
jazz multicam set-sound trailer.jazz <multicam-clip-id> --angle 1

## multicam.switch
jazz multicam switch trailer.jazz <multicam-clip-id> --at 00:00:32.000 --angle 2

## multicam.sync
jazz multicam sync trailer.jazz <capture-id> <cam2-id> --sync audio

## multicam.view
jazz --attach multicam view <multicam-clip-id>

## param.clear-driver
jazz param clear-driver trailer.jazz <clip-id> transform.rotation

## param.clear-keyframes
jazz param clear-keyframes trailer.jazz <clip-id> opacity

## param.get
jazz param get trailer.jazz <clip-id> opacity --at 00:00:02.000

## param.list
jazz param list trailer.jazz <clip-id>

## param.set
jazz param set trailer.jazz <clip-id> transform.position "100, 50"

## param.set-driver
jazz param set-driver trailer.jazz <clip-id> transform.rotation "value + wiggle(2, 12)"

## perf.project
# Builds a project of its own from one recording with a picture and sound.
jazz perf project .\captures\boss-fight.mp4 --clips 2000 --edits 200

## perf.soak
jazz perf soak .\captures\boss-fight.mp4 --hours 2 --csv soak.csv

## playback.clear-in-out
jazz --attach playback clear-in-out

## playback.go-to
jazz --attach playback go-to next-marker

## playback.loop
jazz --attach playback loop on

## playback.pause
jazz --attach playback pause

## playback.play
jazz --attach playback play

## playback.seek
jazz --attach playback seek 00:00:12.500

## playback.set-in
jazz --attach playback set-in --at 00:00:10.000

## playback.set-out
jazz --attach playback set-out --at 00:00:20.000

## playback.set-quality
jazz --attach playback set-quality half

## playback.shuttle
jazz --attach playback shuttle --rate -2

## playback.state
jazz --attach playback state

## playback.step
jazz --attach playback step --frames 5

## playback.stop
jazz --attach playback stop

## playback.toggle
jazz --attach playback toggle

## plugin.add
jazz plugin add trailer.jazz <a1-id> <plugin-id>

## plugin.list
jazz plugin list trailer.jazz

## plugin.params
jazz plugin params trailer.jazz <plugin-effect-id>

## plugin.save-state
jazz plugin save-state trailer.jazz <plugin-effect-id>

## plugin.scan
jazz plugin scan trailer.jazz --again

## presets.add-to-project
# my-youtube is a preset saved on this PC with jazz presets save.
jazz presets add-to-project trailer.jazz my-youtube

## presets.delete
jazz presets delete my-youtube

## presets.get
jazz presets get youtube-4k

## presets.list
jazz presets list --category discord

## presets.remove-from-project
# my-youtube is a preset saved on this PC with jazz presets save.
jazz presets remove-from-project trailer.jazz my-youtube

## presets.save
jazz presets save my-youtube --from youtube-1080p --label "My YouTube" --quality 18 --loudness -14

## project.archive
jazz project archive trailer.jazz trailer-archive.zip --trim --handles 2s

## project.consolidate
jazz project consolidate trailer.jazz D:\Projects\Trailer --trim

## project.get
jazz project get trailer.jazz

## project.set-color-management
jazz project set-color-management trailer.jazz --pipeline aces --output rec709

## project.set-settings
jazz project set-settings trailer.jazz --fps 30000/1001 --size 3840x2160

## project.set-tone-map
jazz project set-tone-map trailer.jazz --operator bt2390

## proxy.generate
jazz proxy generate trailer.jazz --auto

## proxy.list
jazz proxy list trailer.jazz

## proxy.remove
jazz proxy remove trailer.jazz --all

## proxy.set-enabled
jazz proxy set-enabled trailer.jazz true

## recovery.accept
jazz recovery accept trailer.jazz

## recovery.check
jazz recovery check trailer.jazz

## recovery.discard
jazz recovery discard trailer.jazz

## role.add
jazz role add trailer.jazz Commentary --color orange

## role.list
jazz role list trailer.jazz

## role.mute
jazz role mute trailer.jazz Music true

## role.remove
jazz role remove trailer.jazz Commentary --to Dialogue

## role.rename
jazz role rename trailer.jazz Commentary Voiceover

## role.solo
jazz role solo trailer.jazz Dialogue true

## rpc.call
jazz --attach rpc call clip.split '{"clipId": "<clip-id>", "at": "2s"}'

## rpc.list
jazz rpc list

## rpc.events
jazz --attach rpc events --events project.changed,export.progress

## rpc.stdio
jazz --attach rpc stdio

## rpc.instances
jazz rpc instances

## scopes.measure
jazz scopes measure trailer.jazz --at 00:00:12.500

## selection.clear
jazz --attach selection clear

## selection.get
jazz --attach selection get

## selection.set
jazz --attach selection set <clip-id> <next-clip-id> --mode add

## sequence.create
# A new sequence becomes the one shown unless --set-active false says not to.
jazz sequence create trailer.jazz "Vertical cut" --set-active false

## sequence.find-source-frame
jazz sequence find-source-frame trailer.jazz <capture-id> --at 00:01:12.000

## sequence.list
jazz sequence list trailer.jazz

## sequence.reframe
jazz sequence reframe trailer.jazz --size 1080x1920 --mode fit --name "Shorts"

## sequence.remove
jazz sequence remove trailer.jazz <sequence-id>

## sequence.rename
jazz sequence rename trailer.jazz <sequence-id> "Director's cut"

## sequence.set-active
jazz sequence set-active trailer.jazz <sequence-id>

## sequence.set-motion-blur
jazz sequence set-motion-blur trailer.jazz --angle 180 --samples 12

## sequence.set-settings
jazz sequence set-settings trailer.jazz <sequence-id> --size 1080x1920

## settings.get
jazz settings get --section cache

## settings.set
jazz settings set control.tcp true

## shape3d.add
jazz shape3d add trailer.jazz --kind torus --at 0 --dur 4s --color #FF8800 --metallic 0.8

## source.clear-in-out
jazz --attach source clear-in-out

## source.open
jazz --attach source open <capture-id> --at 00:01:10.000

## source.play
jazz --attach source play --play

## source.seek
jazz --attach source seek --at 00:01:12.000

## source.set-in
jazz --attach source set-in --at 00:01:10.000

## source.set-out
jazz --attach source set-out --at 00:01:14.000

## source.state
jazz --attach source state

## speech.transcribe
jazz speech transcribe trailer.jazz <voice-clip-id> --language en

## speech.transcript
jazz speech transcript trailer.jazz <voice-clip-id>

## subtitle.add
jazz subtitle add trailer.jazz <subtitle-track-id> --at 00:00:02.000 --dur 3s --text "Wishlist now"

## subtitle.check
jazz subtitle check trailer.jazz <subtitle-track-id> --max-cps 17

## subtitle.export
jazz subtitle export trailer.jazz <subtitle-track-id> --format vtt --out trailer.vtt

## subtitle.from-transcript
jazz subtitle from-transcript trailer.jazz <subtitle-track-id> --clip <voice-clip-id> --max-chars 42

## subtitle.import
jazz subtitle import trailer.jazz trailer.en.srt --language eng

## subtitle.list
jazz subtitle list trailer.jazz <subtitle-track-id>

## subtitle.read
jazz subtitle read trailer.jazz trailer.en.srt

## subtitle.replace
jazz subtitle replace trailer.jazz <subtitle-track-id> --find "colour" --with "color"

## subtitle.set-align
jazz subtitle set-align trailer.jazz <cue-id> top

## subtitle.set-style
# --size is a fraction of the frame's height.
jazz subtitle set-style trailer.jazz <subtitle-track-id> --font "Segoe UI" --size 0.05 --color #FFFFFF --box #00000099

## subtitle.set-text
jazz subtitle set-text trailer.jazz <cue-id> --text "Out now"

## subtitle.set-time
jazz subtitle set-time trailer.jazz <cue-id> --at 00:00:02.500 --dur 2s

## subtitle.shift
jazz subtitle shift trailer.jazz <subtitle-track-id> --by 250ms

## subtitle.split-long
jazz subtitle split-long trailer.jazz <subtitle-track-id> --max-chars 42 --max-lines 2

## template.apply
jazz template apply trailer.jazz end-card --at 00:00:55.000 --param "headline=Wishlist now,accent=#FF8800"

## template.list
jazz template list trailer.jazz

## template.save-selection
jazz template save-selection trailer.jazz my-lower-third --clips <title-clip-id> --label "My lower third"

## text3d.add
jazz text3d add trailer.jazz --text "GAME OVER" --at 0 --dur 4s --depth 40 --bevel 4 --color #FFCC00

## timeline.describe
jazz timeline describe trailer.jazz --detail full

## timeline.set-magnetic
jazz timeline set-magnetic trailer.jazz true

## title.add
jazz title add trailer.jazz --text "Wishlist now" --preset lower-third --at 00:00:20.000 --dur 4s

## title.list-presets
jazz title list-presets trailer.jazz

## title.measure
jazz title measure trailer.jazz <title-clip-id> --at 00:00:21.000

## title.save-preset
jazz title save-preset trailer.jazz <title-clip-id> --name my-lower-third --label "My lower third"

## title.set-animation
jazz title set-animation trailer.jazz <title-clip-id> --in typewriter --in-dur 1s --out fade

## title.set-style
jazz title set-style trailer.jazz <title-clip-id> --font "Segoe UI" --size 96 --color #FFCC00 --align centre

## title.set-text
jazz title set-text trailer.jazz <title-clip-id> "[b]Out now[/b]\nOn Steam"

## track.add
jazz track add trailer.jazz video --name "Titles"

## track.list
jazz track list trailer.jazz

## track.move
jazz track move trailer.jazz <v2-id> 0

## track.remove
jazz track remove trailer.jazz <v2-id>

## track.rename
jazz track rename trailer.jazz <a1-id> "Dialogue"

## track.set-color
jazz track set-color trailer.jazz <v1-id> #3A6EA5

## track.set-height
jazz track set-height trailer.jazz <v1-id> 96

## track.set-language
jazz track set-language trailer.jazz <subtitle-track-id> fra

## track.set-lock
jazz track set-lock trailer.jazz <v1-id> true

## track.set-matte
jazz track set-matte trailer.jazz <v2-id> --source <v1-id> --mode alpha

## track.set-motion-blur
jazz track set-motion-blur trailer.jazz <v2-id> --angle 180

## track.set-mute
jazz track set-mute trailer.jazz <music-track-id> true

## track.set-pan
jazz track set-pan trailer.jazz <a1-id> --pan 0.2

## track.set-role
jazz track set-role trailer.jazz <music-track-id> Music

## track.set-solo
jazz track set-solo trailer.jazz <a1-id> true

## track.set-sync-lock
jazz track set-sync-lock trailer.jazz <music-track-id> false

## track.set-target
jazz track set-target trailer.jazz <v1-id> true

## track.set-volume
jazz track set-volume trailer.jazz <music-track-id> --db -8

## tracking.apply
# The facecam follows the point, keeping its distance from it.
jazz tracking apply trailer.jazz <point-track-id> --to <facecam-clip-id>

## tracking.point
jazz tracking point trailer.jazz <clip-id> --at 00:00:01.000 --x 820 --y 410 --name "Boss head"

## tracking.remove
jazz tracking remove trailer.jazz <point-track-id>

## transition.add
jazz transition add trailer.jazz <clip-id> <next-clip-id> --type transition.dip --dur 500ms

## transition.add-all
jazz transition add-all trailer.jazz --track <v1-id> --type transition.crossfade --dur 12f --handles trim

## transition.apply-default
jazz transition apply-default trailer.jazz --at-cut 00:00:06.000

## transition.list
jazz transition list trailer.jazz --track <v1-id>

## transition.remove
jazz transition remove trailer.jazz <transition-id>

## transition.set
jazz transition set trailer.jazz <transition-id> --dur 1s --alignment end-of-left

## transition.set-default
jazz transition set-default trailer.jazz transition.crossfade --dur 15f

## transition.set-param
jazz transition set-param trailer.jazz <transition-id> colour #FFFFFF

## trim.add-segment
jazz trim add-segment trailer.jazz --in 00:07:00.000 --out 00:08:30.000

## trim.remove-range
jazz trim remove-range trailer.jazz --in 00:05:00.000 --out 00:05:45.000

## trim.set-segments
jazz trim set-segments trailer.jazz 00:01:00-00:04:30,00:12:00-00:13:30

## trim.start
jazz trim start trailer.jazz <capture-id> --name "Highlights"

## vfx.apply-preset
jazz vfx apply-preset trailer.jazz impact.heavy --at 00:00:42.000

## vfx.find-static
jazz vfx find-static trailer.jazz <clip-id>

## vfx.hide-static
jazz vfx hide-static trailer.jazz <clip-id> --how blur

## vfx.presets
jazz vfx presets trailer.jazz
