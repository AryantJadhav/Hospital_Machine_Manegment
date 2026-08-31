import { useEffect } from 'react';
import { StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { theme } from '../theme';

export type BannerTone = 'ok' | 'warn' | 'error';

/**
 * Confirmation a technician can actually see.
 *
 * Submitting a PM and being returned silently to the scanner is
 * indistinguishable from the submission being lost, and a technician who
 * cannot tell the difference stops trusting the app and goes back to paper.
 */
export function Banner({
  message,
  tone = 'ok',
  onDismiss,
  autoDismissMs,
}: {
  message: string;
  tone?: BannerTone;
  onDismiss: () => void;
  autoDismissMs?: number;
}) {
  useEffect(() => {
    if (!autoDismissMs) return;
    const t = setTimeout(onDismiss, autoDismissMs);
    return () => clearTimeout(t);
  }, [autoDismissMs, onDismiss, message]);

  const background =
    tone === 'ok' ? '#17351f' : tone === 'warn' ? '#33280f' : '#3a1a1c';
  const colour =
    tone === 'ok' ? theme.ok : tone === 'warn' ? '#e0b071' : theme.danger;

  return (
    <View style={[styles.banner, { backgroundColor: background, borderColor: colour }]}>
      <Text style={[styles.text, { color: colour }]}>{message}</Text>
      <TouchableOpacity onPress={onDismiss} hitSlop={12}>
        <Text style={[styles.dismiss, { color: colour }]}>Dismiss</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    marginHorizontal: 12,
    marginTop: 10,
    padding: 12,
    borderRadius: 8,
    borderWidth: 1,
  },
  text: { flex: 1, fontSize: 14, lineHeight: 19 },
  dismiss: { fontSize: 13, fontWeight: '600' },
});
