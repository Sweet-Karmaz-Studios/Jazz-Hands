# Masks

A mask limits a clip's picture, or one of its effects, to a shape: a rectangle, an ellipse, or a polygon or bezier path in SVG path data. Bounds are in the source picture's pixels from its top left corner. `feather` softens the edge, `expansion` grows or shrinks the shape, `invert` keeps the outside, and several masks combine by `mode`: add, subtract or intersect.

On a clip, a mask cuts the picture out (a picture in picture with a rounded corner, a spotlight). On an effect, it limits where the effect applies (blur only a face, colour only the sky). `mask_set` changes one; its bounds and feather are parameters, so they can be keyframed to follow something that moves. Check the result with `render_frame`.

A mask's `path` can be keyframed like any parameter, and between two keyframes the outline morphs point by point: each outline is read as curves, the one with fewer points has its longest edges split until they match, and every point moves in a straight line. Draw the second shape by moving the first one's points and the morph is the one you expect. Outlines with a different number of separate shapes hold instead.
