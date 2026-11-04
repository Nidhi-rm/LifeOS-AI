package com.lifeos.workspace;

import android.content.ContentValues;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Base64;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.OutputStream;

@CapacitorPlugin(name = "BackupSaver")
public class BackupSaverPlugin extends Plugin {
    @PluginMethod
    public void saveToDownloads(PluginCall call) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            call.reject("Saving backups to Downloads requires Android 10 or newer.");
            return;
        }

        String requestedName = call.getString("fileName");
        String base64 = call.getString("base64");
        if (requestedName == null || base64 == null) {
            call.reject("The backup file data is missing.");
            return;
        }

        String fileName = requestedName.replaceAll("[^A-Za-z0-9._-]", "_");
        if (!fileName.toLowerCase().endsWith(".zip")) {
            fileName += ".zip";
        }

        Uri fileUri = null;
        try {
            byte[] data = Base64.decode(base64, Base64.DEFAULT);
            ContentValues values = new ContentValues();
            values.put(MediaStore.Downloads.DISPLAY_NAME, fileName);
            values.put(MediaStore.Downloads.MIME_TYPE, "application/zip");
            values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
            values.put(MediaStore.Downloads.IS_PENDING, 1);

            fileUri = getContext().getContentResolver().insert(
                    MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
            if (fileUri == null) {
                throw new IllegalStateException("Android could not create the backup file.");
            }

            try (OutputStream output = getContext().getContentResolver().openOutputStream(fileUri)) {
                if (output == null) {
                    throw new IllegalStateException("Android could not open the backup file.");
                }
                output.write(data);
            }

            ContentValues ready = new ContentValues();
            ready.put(MediaStore.Downloads.IS_PENDING, 0);
            getContext().getContentResolver().update(fileUri, ready, null, null);

            JSObject result = new JSObject();
            result.put("fileName", fileName);
            result.put("uri", fileUri.toString());
            call.resolve(result);
        } catch (Exception error) {
            if (fileUri != null) {
                getContext().getContentResolver().delete(fileUri, null, null);
            }
            call.reject("Could not save the backup to Downloads: " + error.getMessage(), error);
        }
    }
}
