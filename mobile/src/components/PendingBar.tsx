import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { queue } from '../queue';
import { theme } from '../theme';

/**
 * How many completed PMs are still sitting on this device.
 *
 * Without this the queue is invisible: a technician finishes a round with
 * six PMs in a dead spot, walks back into signal, and has no way to know
 * whether the work ever reached the server. Silence is exactly what a lost
 * submission looks like, so the count is shown and a retry is one tap away.
 */
export function PendingBar({ onFlushed }: { onFlushed?: (sent: number, failed: number) => void }) {
  const [count, setCount] = useState(0);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => setCount(await queue.count()), []);

  useEffect(() => {
    void refresh();

    // Polled rather than pushed. The queue is written from several screens,
    // and a 4-second poll is far cheaper than threading a subscription
    // through every one of them.
    const timer = setInterval(() => void refresh(), 4000);
    return () => clearInterval(timer);
  }, [refresh]);

  if (count === 0) {
    return null;
  }

  async function send() {
    setBusy(true);
    try {
      const result = await queue.flush();
      await refresh();
      onFlushed?.(result.sent, result.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.bar}>
      <Text style={styles.text}>
        {count} completed PM{count === 1 ? '' : 's'} waiting to send
      </Text>

      <TouchableOpacity style={styles.button} onPress={() => void send()} disabled={busy}>
        {busy ? (
          <ActivityIndicator color="#fff" size="small" />
        ) : (
          <Text style={styles.buttonText}>Send now</Text>
        )}
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 10,
    backgroundColor: '#33280f',
    borderBottomWidth: 1,
    borderBottomColor: '#5a4520',
  },
  text: { flex: 1, color: '#e0b071', fontSize: 14 },
  button: {
    backgroundColor: theme.accent,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 6,
    minWidth: 88,
    alignItems: 'center',
  },
  buttonText: { color: '#fff', fontSize: 14, fontWeight: '600' },
});
