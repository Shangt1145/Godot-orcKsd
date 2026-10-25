package com.godot.game;

import android.app.*;
import android.content.*;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.*;
import java.io.*;

/** Keeps an explicitly hosted room alive while the player switches apps. No automatic restart. */
public final class RoomHostService extends Service {
    private static void copyAssets(Context context, String source, File target) throws IOException {
        String[] children = context.getAssets().list(source);
        if (children.length > 0) {
            if (!target.isDirectory() && !target.mkdirs()) throw new IOException("无法创建运行文件目录");
            for (String child : children) copyAssets(context, source + "/" + child, new File(target, child));
        } else {
            try (InputStream input = context.getAssets().open(source); OutputStream output = new FileOutputStream(target)) {
                byte[] buffer = new byte[65536]; int read;
                while ((read = input.read(buffer)) != -1) output.write(buffer, 0, read);
            }
        }
    }
    private PowerManager.WakeLock wake;
    private WifiManager.MulticastLock multicast;
    private static WifiManager.MulticastLock discovery;
    public static String documentUri(Context context, String path) {
        return androidx.core.content.FileProvider.getUriForFile(context, context.getPackageName() + ".fileprovider", new File(path)).toString();
    }
    public static String scriptReferences(Context context) {
        try {
            String abi = android.os.Process.is64Bit() && Build.SUPPORTED_ABIS[0].contains("x86") ? "x86_64" : "arm64";
            String source = ".godot/mono/publish/" + abi;
            File target = new File(context.getCodeCacheDir(), "script-references");
            if (!target.isDirectory() && !target.mkdirs()) throw new IOException("无法创建效果编译缓存");
            String version = Long.toString(context.getPackageManager().getPackageInfo(context.getPackageName(), 0).lastUpdateTime);
            File stamp = new File(target, "version");
            if (stamp.isFile()) {
                try (BufferedReader reader = new BufferedReader(new FileReader(stamp))) {
                    if (version.equals(reader.readLine())) return target.getAbsolutePath();
                }
            }
            copyAssets(context, source, target);
            if (!new File(target, "System.Private.CoreLib.dll").isFile()) throw new IOException("安装包缺少效果编译引用");
            try (OutputStream output = new FileOutputStream(stamp)) { output.write(version.getBytes(java.nio.charset.StandardCharsets.UTF_8)); }
            return target.getAbsolutePath();
        } catch (Exception error) { throw new IllegalStateException("无法加载效果编译引用", error); }
    }
    public static void setDiscovery(Context context, boolean enabled) {
        if (enabled && discovery == null) {
            WifiManager wifi = (WifiManager)context.getApplicationContext().getSystemService(WIFI_SERVICE);
            if (wifi != null) { discovery = wifi.createMulticastLock("OrC-KSD:lobby"); discovery.setReferenceCounted(false); discovery.acquire(); }
        } else if (!enabled && discovery != null) { if (discovery.isHeld()) discovery.release(); discovery = null; }
    }
    public static boolean start(Context context) {
        Intent intent = new Intent(context, RoomHostService.class);
        if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent); else context.startService(intent);
        return true;
    }
    public static void stop(Context context) { context.stopService(new Intent(context, RoomHostService.class)); }
    public static void installApk(Context context, String document) {
        Uri uri = Uri.parse(document);
        if (!"content".equals(uri.getScheme())) throw new IllegalArgumentException("请通过系统文档选择器选择 APK");
        Intent intent = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_GRANT_READ_URI_PERMISSION);
        context.startActivity(intent);
    }
    @Override public void onCreate() {
        super.onCreate();
        String channel = "orc_hosted_room";
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(new NotificationChannel(channel, "联机房间", NotificationManager.IMPORTANCE_LOW));
        PendingIntent launch = PendingIntent.getActivity(this, 0, getPackageManager().getLaunchIntentForPackage(getPackageName()), PendingIntent.FLAG_IMMUTABLE);
        Notification.Builder notification = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, channel) : new Notification.Builder(this);
        startForeground(4717, notification.setContentTitle("OrC-KSD 房间运行中")
            .setContentText("好友可继续连接。离开房间后停止运行。")
            .setSmallIcon(getApplicationInfo().icon).setContentIntent(launch).setOngoing(true).build());
        wake = ((PowerManager)getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "OrC-KSD:room");
        wake.acquire();
        WifiManager wifi = (WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
        if (wifi != null) { multicast = wifi.createMulticastLock("OrC-KSD:discovery"); multicast.setReferenceCounted(false); multicast.acquire(); }
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) { return START_NOT_STICKY; }
    @Override public void onDestroy() {
        if (multicast != null && multicast.isHeld()) multicast.release();
        if (wake != null && wake.isHeld()) wake.release();
        stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
