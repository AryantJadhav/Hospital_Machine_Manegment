import { useRef, useState } from 'react';
import { PanResponder, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import Svg, { Path } from 'react-native-svg';
import { theme } from '../theme';

/**
 * Finger-drawn signature.
 *
 * Captured as SVG paths and handed to the caller as such, rather than
 * rasterising on the device. A phone canvas-to-PNG round trip needs either a
 * WebView or a native module, and the strokes are what actually matter — the
 * server stores whatever the device sends, so a compact, exact
 * representation is better than a blurry bitmap.
 */
export function SignaturePad({
  onChange,
  height = 180,
}: {
  onChange: (paths: string[]) => void;
  height?: number;
}) {
  const [paths, setPaths] = useState<string[]>([]);
  const current = useRef<string>('');
  const [live, setLive] = useState<string>('');

  const responder = useRef(
    PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,

      onPanResponderGrant: (e) => {
        const { locationX, locationY } = e.nativeEvent;
        current.current = `M${locationX.toFixed(1)},${locationY.toFixed(1)}`;
        setLive(current.current);
      },

      onPanResponderMove: (e) => {
        const { locationX, locationY } = e.nativeEvent;
        current.current += ` L${locationX.toFixed(1)},${locationY.toFixed(1)}`;
        setLive(current.current);
      },

      onPanResponderRelease: () => {
        const finished = current.current;
        current.current = '';
        setLive('');

        if (finished.length > 0) {
          setPaths((prev) => {
            const next = [...prev, finished];
            onChange(next);
            return next;
          });
        }
      },
    }),
  ).current;

  function clear() {
    setPaths([]);
    setLive('');
    current.current = '';
    onChange([]);
  }

  return (
    <View>
      <View style={[styles.pad, { height }]} {...responder.panHandlers}>
        <Svg width="100%" height="100%">
          {paths.map((d, i) => (
            <Path key={i} d={d} stroke={theme.text} strokeWidth={2.5} fill="none"
                  strokeLinecap="round" strokeLinejoin="round" />
          ))}
          {live.length > 0 && (
            <Path d={live} stroke={theme.text} strokeWidth={2.5} fill="none"
                  strokeLinecap="round" strokeLinejoin="round" />
          )}
        </Svg>

        {paths.length === 0 && live.length === 0 && (
          <Text style={styles.placeholder} pointerEvents="none">
            Sign here
          </Text>
        )}
      </View>

      <TouchableOpacity onPress={clear} style={styles.clear}>
        <Text style={styles.clearText}>Clear signature</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  pad: {
    borderWidth: 1,
    borderColor: theme.border,
    borderRadius: 8,
    backgroundColor: theme.surface,
    overflow: 'hidden',
    justifyContent: 'center',
  },
  placeholder: { position: 'absolute', alignSelf: 'center', color: theme.muted, fontSize: 15 },
  clear: { alignSelf: 'flex-end', paddingVertical: 8 },
  clearText: { color: theme.accent, fontSize: 14 },
});
